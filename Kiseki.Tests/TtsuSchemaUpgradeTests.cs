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
}
