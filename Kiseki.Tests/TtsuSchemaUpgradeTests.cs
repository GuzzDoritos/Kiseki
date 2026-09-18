using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class TtsuSchemaUpgradeTests
{
    [Fact]
    public async Task ExistingSqliteDatabase_AdditiveUpgradePreservesDuplicatesAndOrphans()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Legacy book");
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        // Reproduce the previous local schema, keeping the remaining library tables intact.
        await db.Context.Database.ExecuteSqlRawAsync("""
            DROP TABLE "ImmersionLogs";
            DROP TABLE "TtsuBindings";
            DROP TABLE "TtsuImportReceipts";
            CREATE TABLE "TtsuBindings" (
                "MediaWorkId" TEXT NOT NULL PRIMARY KEY REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE,
                "OriginalTitle" TEXT NOT NULL, "FolderHint" TEXT NULL, "Version" TEXT NOT NULL);
            CREATE TABLE "TtsuImportReceipts" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Books" INTEGER NOT NULL,
                "AddedDays" INTEGER NOT NULL, "UpdatedDays" INTEGER NOT NULL,
                "UnchangedDays" INTEGER NOT NULL, "StaleDays" INTEGER NOT NULL);
            CREATE TABLE "ImmersionLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Date" TEXT NOT NULL,
                "CharactersRead" INTEGER NOT NULL, "TimeSpentMinutes" REAL NOT NULL,
                "Source" TEXT NOT NULL, "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id"));
            """);
        var bindingVersion = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"TtsuBindings\" VALUES ({work.Id}, {"LEGACY BOOK"}, {"Legacy book"}, {bindingVersion})");
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"TtsuImportReceipts\" VALUES ({receiptId}, {1}, {1}, {0}, {0}, {0})");
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var orphan = Guid.NewGuid();
        foreach (var id in new[] { first, second, orphan })
        {
            Guid? target = id == orphan ? null : work.Id;
            await db.Context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ImmersionLogs\" VALUES ({id}, {"2026-09-01"}, {100}, {1d}, {"ttsu"}, {target})");
        }
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        db.Context.ChangeTracker.Clear();
        var logs = await db.Context.ImmersionLogs.ToListAsync();
        Assert.Equal(3, logs.Count);
        Assert.All(logs, x => { Assert.Null(x.SourceRevision); Assert.Null(x.TtsuBindingId); });
        Assert.Equal(2, logs.Count(x => x.MediaWorkId == work.Id));
        var upgradedWork = await db.Context.MediaWorks.SingleAsync();
        var upgradedBinding = await db.Context.TtsuBindings.SingleAsync();
        var upgradedReceipt = await db.Context.TtsuImportReceipts.SingleAsync();
        Assert.Null(upgradedWork.TtsuCharacterCount);
        Assert.Equal(bindingVersion, upgradedBinding.Version);
        Assert.Null(upgradedBinding.ProgressFraction);
        Assert.Equal(0, upgradedReceipt.ProgressUpdates);
        Assert.Equal(0, upgradedReceipt.CharacterTotalUpdates);
        Assert.Equal(0, upgradedReceipt.MetadataLinks);
        Assert.Equal(0, upgradedReceipt.MetadataSkips);
        Assert.Single(await db.Service.GetOrphansAsync());
        Assert.False((await db.Service.PreviewAsync(TtsuMergeServiceTests.Book(), work.Id)).CanApply);
    }

    [Fact]
    public async Task MetadataReceiptCounts_UpgradesLegacyReceiptsWithoutDataLoss_AndIsIdempotent()
    {
        await using var db = await ImportDatabase.CreateAsync();
        // Drop and recreate receipts table without metadata columns
        await db.Context.Database.ExecuteSqlRawAsync("""
            DROP TABLE "TtsuImportReceipts";
            CREATE TABLE "TtsuImportReceipts" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Books" INTEGER NOT NULL,
                "AddedDays" INTEGER NOT NULL,
                "UpdatedDays" INTEGER NOT NULL,
                "UnchangedDays" INTEGER NOT NULL,
                "StaleDays" INTEGER NOT NULL,
                "ProgressUpdates" INTEGER NOT NULL DEFAULT 0,
                "CharacterTotalUpdates" INTEGER NOT NULL DEFAULT 0);
            """);

        var legacyReceiptId = Guid.NewGuid();
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"TtsuImportReceipts\" VALUES ({legacyReceiptId}, 2, 5, 1, 3, 0, 2, 1)");

        // Running upgrade twice must succeed without error (idempotent)
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);

        db.Context.ChangeTracker.Clear();
        var upgraded = await db.Context.TtsuImportReceipts.SingleAsync(r => r.Id == legacyReceiptId);
        Assert.Equal(2, upgraded.Books);
        Assert.Equal(5, upgraded.AddedDays);
        Assert.Equal(1, upgraded.UpdatedDays);
        Assert.Equal(3, upgraded.UnchangedDays);
        Assert.Equal(0, upgraded.StaleDays);
        Assert.Equal(2, upgraded.ProgressUpdates);
        Assert.Equal(1, upgraded.CharacterTotalUpdates);
        Assert.Equal(0, upgraded.MetadataLinks);
        Assert.Equal(0, upgraded.MetadataSkips);

        // Verify that a new receipt with non-zero metadata counts can be added and queried
        var newReceipt = new TtsuImportReceipt
        {
            Id = Guid.NewGuid(),
            Books = 1,
            AddedDays = 1,
            MetadataLinks = 1,
            MetadataSkips = 0
        };
        db.Context.TtsuImportReceipts.Add(newReceipt);
        await db.Context.SaveChangesAsync();

        db.Context.ChangeTracker.Clear();
        var queried = await db.Context.TtsuImportReceipts.SingleAsync(r => r.Id == newReceipt.Id);
        Assert.Equal(1, queried.MetadataLinks);
        Assert.Equal(0, queried.MetadataSkips);
    }

    [Fact]
    public async Task BoundDailyRows_EnforceWorkSourceAndUniqueness()
    {
        await using var db = await ImportDatabase.CreateAsync();
        await TtsuMergeServiceTests.Apply(db.Service, TtsuMergeServiceTests.Book(TtsuMergeServiceTests.Entry(100, 1)));
        var workId = (await db.Context.MediaWorks.SingleAsync()).Id;
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = workId, TtsuBindingId = workId, Date = new(2026, 9, 1) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        db.Context.ChangeTracker.Clear();
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = workId, TtsuBindingId = workId, Date = new(2026, 9, 2), Source = "manual" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        db.Context.ChangeTracker.Clear();
        var another = new MediaWork("Another"); db.Context.Add(another); await db.Context.SaveChangesAsync();
        db.Context.ImmersionLogs.Add(new() { MediaWorkId = another.Id, TtsuBindingId = workId, Date = new(2026, 9, 2) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task CoverProvenance_UpgradesLegacyJitenCoverUrl_PreservesNonNullAndNullRows_AndIsIdempotent()
    {
        await using var db = await ImportDatabase.CreateAsync();

        // Create legacy schema with JitenCoverUrl on MediaWorks
        await db.Context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "ImmersionLogs";
            DROP TABLE IF EXISTS "TtsuBindings";
            DROP TABLE IF EXISTS "MediaWorks";
            CREATE TABLE "MediaWorks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Title" TEXT NOT NULL,
                "MediaType" INTEGER NOT NULL DEFAULT 1,
                "MediaSeriesId" TEXT NULL,
                "JitenDeckId" INTEGER NULL,
                "JitenSubdeckId" INTEGER NULL,
                "JitenCoverUrl" TEXT NULL,
                "JitenCharacterCount" INTEGER NULL,
                "TtsuCharacterCount" INTEGER NULL,
                "ManualCharacterCountOverride" INTEGER NULL,
                "IsCompleted" INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE "TtsuBindings" (
                "MediaWorkId" TEXT NOT NULL PRIMARY KEY REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE,
                "OriginalTitle" TEXT NOT NULL, "FolderHint" TEXT NULL, "Version" TEXT NOT NULL
            );
            CREATE TABLE "ImmersionLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Date" TEXT NOT NULL,
                "CharactersRead" INTEGER NOT NULL,
                "TimeSpentMinutes" REAL NOT NULL,
                "Source" TEXT NOT NULL,
                "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id")
            );
            """);

        var work1Id = Guid.NewGuid();
        var work2Id = Guid.NewGuid();
        var logId = Guid.NewGuid();

        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"JitenCoverUrl\", \"JitenCharacterCount\") VALUES ({work1Id}, {"Book With Legacy Cover"}, {1}, {"https://example.com/legacy-cover.jpg"}, {100000})");
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"JitenCoverUrl\", \"JitenCharacterCount\") VALUES ({work2Id}, {"Book Without Cover"}, {1}, {null}, {50000})");
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"TtsuBindings\" VALUES ({work1Id}, {"ORIGINAL TITLE"}, {"hint"}, {Guid.NewGuid()})");
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"ImmersionLogs\" VALUES ({logId}, {"2026-09-01"}, {200}, {15.0}, {"ttsu"}, {work1Id})");

        // Run upgrade twice (verifying idempotence)
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);

        db.Context.ChangeTracker.Clear();

        var upgraded1 = await db.Context.MediaWorks.Include(w => w.Logs).SingleAsync(w => w.Id == work1Id);
        Assert.Equal("Book With Legacy Cover", upgraded1.Title);
        Assert.Equal("https://example.com/legacy-cover.jpg", upgraded1.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, upgraded1.CoverSource);
        Assert.Equal(100000, upgraded1.JitenCharacterCount);
        Assert.Single(upgraded1.Logs);
        Assert.Equal(200, upgraded1.Logs[0].CharactersRead);

        var upgraded2 = await db.Context.MediaWorks.SingleAsync(w => w.Id == work2Id);
        Assert.Equal("Book Without Cover", upgraded2.Title);
        Assert.Null(upgraded2.CoverUrl);
        Assert.Equal(MediaCoverSource.None, upgraded2.CoverSource);
        Assert.Equal(50000, upgraded2.JitenCharacterCount);

        var binding = await db.Context.TtsuBindings.SingleAsync(b => b.MediaWorkId == work1Id);
        Assert.Equal("ORIGINAL TITLE", binding.OriginalTitle);
    }

    [Fact]
    public async Task CoverProvenance_UpgradesPartialState_WhereCoverUrlExistsButCoverSourceIsMissing()
    {
        await using var db = await ImportDatabase.CreateAsync();

        // Partial state: CoverUrl exists, but CoverSource is not yet added
        await db.Context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "ImmersionLogs";
            DROP TABLE IF EXISTS "TtsuBindings";
            DROP TABLE IF EXISTS "MediaWorks";
            CREATE TABLE "MediaWorks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Title" TEXT NOT NULL,
                "MediaType" INTEGER NOT NULL DEFAULT 1,
                "MediaSeriesId" TEXT NULL,
                "JitenDeckId" INTEGER NULL,
                "JitenSubdeckId" INTEGER NULL,
                "CoverUrl" TEXT NULL,
                "JitenCharacterCount" INTEGER NULL,
                "TtsuCharacterCount" INTEGER NULL,
                "ManualCharacterCountOverride" INTEGER NULL,
                "IsCompleted" INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE "TtsuBindings" (
                "MediaWorkId" TEXT NOT NULL PRIMARY KEY REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE,
                "OriginalTitle" TEXT NOT NULL, "FolderHint" TEXT NULL, "Version" TEXT NOT NULL
            );
            CREATE TABLE "ImmersionLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "Date" TEXT NOT NULL,
                "CharactersRead" INTEGER NOT NULL,
                "TimeSpentMinutes" REAL NOT NULL,
                "Source" TEXT NOT NULL,
                "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id")
            );
            """);

        var work1Id = Guid.NewGuid();
        var work2Id = Guid.NewGuid();

        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverUrl\") VALUES ({work1Id}, {"Partial Book 1"}, {1}, {"https://example.com/partial.jpg"})");
        await db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverUrl\") VALUES ({work2Id}, {"Partial Book 2"}, {1}, {null})");

        await SqliteSchemaUpgrade.ApplyAsync(db.Context);
        await SqliteSchemaUpgrade.ApplyAsync(db.Context);

        db.Context.ChangeTracker.Clear();

        var upgraded1 = await db.Context.MediaWorks.SingleAsync(w => w.Id == work1Id);
        Assert.Equal("https://example.com/partial.jpg", upgraded1.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, upgraded1.CoverSource);

        var upgraded2 = await db.Context.MediaWorks.SingleAsync(w => w.Id == work2Id);
        Assert.Null(upgraded2.CoverUrl);
        Assert.Equal(MediaCoverSource.None, upgraded2.CoverSource);
    }

    [Fact]
    public async Task EnsureCreated_EnforcesCoverUrlAndSourceConsistencyConstraint()
    {
        await using var db = await ImportDatabase.CreateAsync();

        var workValid = new MediaWork("Valid Work");
        workValid.UpdateCoverUrl("https://example.com/valid.jpg");
        db.Context.MediaWorks.Add(workValid);
        await db.Context.SaveChangesAsync();

        // Inserting invalid raw SQL: CoverUrl is not null, but CoverSource is 0 (None)
        var invalidId1 = Guid.NewGuid();
        var ex1 = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.Context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverUrl\", \"CoverSource\", \"IsCompleted\") VALUES ({invalidId1}, {"Bad 1"}, {1}, {"https://example.com/bad.jpg"}, {0}, {0})"));
        Assert.Contains("CK_MediaWorks_CoverUrlAndSource", ex1.Message);

        // Inserting invalid raw SQL: CoverUrl is null, but CoverSource is 1 (LegacyUnknown)
        var invalidId2 = Guid.NewGuid();
        var ex2 = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.Context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"CoverUrl\", \"CoverSource\", \"IsCompleted\") VALUES ({invalidId2}, {"Bad 2"}, {1}, {null}, {1}, {0})"));
        Assert.Contains("CK_MediaWorks_CoverUrlAndSource", ex2.Message);
    }
}
