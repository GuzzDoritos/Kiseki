using System.Data.Common;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kiseki.Tests;

public sealed class CanonicalInstallmentSchemaTests
{
    [Fact]
    public async Task EmptySqliteDatabase_StartupUpgradeCreatesCurrentSchema()
    {
        var path = TempDatabasePath();
        try
        {
            await UpgradeAsync(path);
            await using var context = Context(path);
            var installment = new MediaInstallment("Fresh", MediaType.Book);
            var work = new MediaWork("Fresh copy") { MediaInstallment = installment };
            context.Add(work);
            await context.SaveChangesAsync();

            Assert.Equal(installment.Id, work.MediaInstallmentId);
            Assert.Single(await context.MediaInstallments.AsNoTracking().ToListAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FreshSqliteSchema_EnforcesInstallmentDeletionAndIdentityUniqueness()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options;
        await using var context = new ImmersionDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book) { MediaSeries = series };
        var copy = new MediaWork("Copy") { MediaInstallment = installment, MediaSeries = series };
        installment.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "deck:10",
            ProviderItemId = 10
        });
        context.Add(copy);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"MediaInstallments\" WHERE \"Id\" = {installment.Id}"));

        context.ChangeTracker.Clear();
        context.InstallmentProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "deck:10",
            MediaInstallmentId = installment.Id,
            ProviderItemId = 10
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ExistingSqliteSchema_BackfillPreservesDataAndLeavesDuplicateClaimsUnassigned()
    {
        var path = TempDatabasePath();
        try
        {
            var fixture = await CreateLegacyDatabaseAsync(path);
            await UpgradeAsync(path);
            await UpgradeAsync(path);

            await using var context = Context(path);
            var works = await context.MediaWorks.AsNoTracking().OrderBy(work => work.Id).ToListAsync();
            var installments = await context.MediaInstallments.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
            Assert.Equal(6, works.Count);
            Assert.Equal(works.Select(work => work.Id), installments.Select(item => item.Id));
            Assert.All(works, work => Assert.Equal(work.Id, work.MediaInstallmentId));
            Assert.All(works, work => Assert.Equal(work.Id, work.Version));

            var standalone = works.Single(work => work.Id == fixture.StandaloneId);
            Assert.Null(standalone.MediaSeriesId);
            Assert.Equal(0, standalone.ManualCharacterCountOverride);
            Assert.Equal(123_456, standalone.TtsuCharacterCount);
            Assert.Equal("https://example.com/manual.jpg", standalone.CoverUrl);
            Assert.Equal(MediaCoverSource.UserOverride, standalone.CoverSource);

            var uniqueDeck = installments.Single(item => item.Id == fixture.UniqueDeckId);
            Assert.Equal(fixture.SeriesId, uniqueDeck.MediaSeriesId);
            Assert.Equal(80_000, uniqueDeck.CanonicalCharacterCount);
            Assert.Equal(CanonicalCoverSource.ProviderExact, uniqueDeck.CanonicalCoverSource);
            Assert.Equal("https://cdn.jiten.moe/deck.jpg", uniqueDeck.CanonicalCoverUrl);

            var uniqueChild = installments.Single(item => item.Id == fixture.UniqueChildId);
            Assert.Equal(CanonicalCoverSource.ProviderParentFallback, uniqueChild.CanonicalCoverSource);
            Assert.Equal("https://cdn.jiten.moe/parent.jpg", uniqueChild.CanonicalCoverUrl);

            var identities = await context.InstallmentProviderIdentities.AsNoTracking()
                .OrderBy(identity => identity.NormalizedKey).ToListAsync();
            Assert.Equal(["deck:30", "subdeck:10:11"], identities.Select(identity => identity.NormalizedKey));
            Assert.DoesNotContain(identities, identity => identity.NormalizedKey == "subdeck:20:21");
            Assert.Equal(2, await context.InstallmentProviderSnapshots.CountAsync());
            Assert.All(identities, identity =>
            {
                Assert.Null(identity.LastSeenAtUtc);
                Assert.Null(identity.MissingSinceUtc);
            });
            Assert.Empty(await context.JitenCatalogueRefreshReceipts.ToListAsync());
            Assert.Empty(await context.JitenFranchiseGraphNodeStates.ToListAsync());
            Assert.Empty(await context.JitenFranchiseTopologyReceipts.ToListAsync());
            Assert.All(installments, item => Assert.Null(item.CharacterCountOverride));

            var duplicateInstallments = installments
                .Where(item => item.Id == fixture.DuplicateOneId || item.Id == fixture.DuplicateTwoId)
                .ToList();
            Assert.Equal(2, duplicateInstallments.Count);
            Assert.All(duplicateInstallments, item => Assert.Null(item.CanonicalCharacterCount));

            var mismatchedWork = works.Single(work => work.Id == fixture.MismatchedTypeId);
            var mismatchedInstallment = installments.Single(item => item.Id == fixture.MismatchedTypeId);
            Assert.Equal(MediaType.Anime, mismatchedWork.MediaType);
            Assert.Equal(MediaType.Anime, mismatchedInstallment.MediaType);
            Assert.Equal(fixture.SeriesId, mismatchedInstallment.MediaSeriesId);
            Assert.Equal(MediaType.Book, (await context.MediaSeries.AsNoTracking()
                .SingleAsync(series => series.Id == fixture.SeriesId)).MediaType);

            var logs = await context.ImmersionLogs.AsNoTracking().OrderBy(log => log.Id).ToListAsync();
            Assert.Equal(fixture.LogIds.Order(), logs.Select(log => log.Id).Order());
            Assert.Single(logs, log => log.MediaWorkId is null);
            var binding = await context.TtsuBindings.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.StandaloneId, binding.MediaWorkId);
            Assert.Equal(0.5, binding.ProgressFraction);
            var receipt = await context.TtsuImportReceipts.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.ReceiptId, receipt.Id);
            Assert.Equal(7, receipt.AddedDays);
            Assert.Equal(2, receipt.MetadataLinks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingSqliteSchema_InterruptedUpgradeRollsBackAndRetryConverges()
    {
        var path = TempDatabasePath();
        try
        {
            await CreateLegacyDatabaseAsync(path);
            var options = new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(ConnectionString(path))
                .AddInterceptors(new ThrowBeforeIndexesInterceptor())
                .Options;
            await using (var interrupted = new ImmersionDbContext(options))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteSchemaUpgrade.ApplyAsync(interrupted));
            }

            await UpgradeAsync(path);
            await using var verify = Context(path);
            Assert.Equal(6, await verify.MediaWorks.CountAsync());
            Assert.Equal(6, await verify.MediaInstallments.CountAsync());
            Assert.All(await verify.MediaWorks.AsNoTracking().ToListAsync(),
                work => Assert.Equal(work.Id, work.MediaInstallmentId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingSqliteSchema_ConcurrentStartupCreatesNoDuplicates()
    {
        var path = TempDatabasePath();
        try
        {
            await CreateLegacyDatabaseAsync(path);
            await Task.WhenAll(UpgradeAsync(path), UpgradeAsync(path));

            await using var verify = Context(path);
            Assert.Equal(6, await verify.MediaInstallments.CountAsync());
            Assert.Equal(2, await verify.InstallmentProviderIdentities.CountAsync());
            Assert.Equal(2, await verify.InstallmentProviderSnapshots.CountAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RepeatedStartup_PreservesHydratedInstallmentsWhoseIdsAreNotLegacyWorkIds()
    {
        var path = TempDatabasePath();
        try
        {
            await UpgradeAsync(path);
            Guid installmentId;
            Guid workId;
            await using (var context = Context(path))
            {
                var series = new MediaSeries("Hydrated", MediaType.Book) { JitenDeckId = 90 };
                var installment = new MediaInstallment("Provider volume", MediaType.Book, 100)
                {
                    MediaSeries = series,
                    CanonicalTitle = "Provider volume",
                    CanonicalCharacterCount = 90_000
                };
                installment.ProviderIdentities.Add(new InstallmentProviderIdentity
                {
                    Provider = "jiten",
                    NormalizedKey = "subdeck:90:901",
                    ProviderItemId = 901,
                    ParentProviderItemId = 90,
                    MediaInstallment = installment
                });
                var work = new MediaWork("Tracked edition")
                {
                    MediaInstallment = installment,
                    MediaSeries = series
                };
                context.AddRange(series, installment, work);
                await context.SaveChangesAsync();
                installmentId = installment.Id;
                workId = work.Id;
            }
            Assert.NotEqual(installmentId, workId);

            await UpgradeAsync(path);
            await UpgradeAsync(path);

            await using var verify = Context(path);
            var stored = await verify.MediaInstallments.AsNoTracking().SingleAsync();
            Assert.Equal(installmentId, stored.Id);
            Assert.Equal("Provider volume", stored.CanonicalTitle);
            Assert.Equal(90_000, stored.CanonicalCharacterCount);
            Assert.Equal(CanonicalCoverSource.None, stored.CanonicalCoverSource);
            Assert.Single(await verify.MediaWorks.AsNoTracking().ToListAsync());
            Assert.Single(await verify.InstallmentProviderIdentities.AsNoTracking().ToListAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task UpgradeAsync(string path)
    {
        await using var context = Context(path);
        await SqliteSchemaUpgrade.ApplyAsync(context);
    }

    private static ImmersionDbContext Context(string path) => new(
        new DbContextOptionsBuilder<ImmersionDbContext>()
            .UseSqlite(ConnectionString(path))
            .Options);

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        DefaultTimeout = 30,
        Pooling = false
    }.ToString();

    private static string TempDatabasePath() => Path.Combine(
        Path.GetTempPath(), $"kiseki-installment-test-{Guid.NewGuid():N}.db");

    private static async Task<LegacyFixture> CreateLegacyDatabaseAsync(string path)
    {
        var fixture = new LegacyFixture(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;");
        await ExecuteAsync(connection, """
            CREATE TABLE "Franchises" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL, "JitenAnchorDeckId" INTEGER NULL);
            CREATE TABLE "MediaSeries" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL, "MediaType" INTEGER NOT NULL,
                "FranchiseId" TEXT NULL REFERENCES "Franchises" ("Id") ON DELETE SET NULL,
                "JitenDeckId" INTEGER NULL);
            CREATE TABLE "MediaWorks" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Title" TEXT NOT NULL, "MediaType" INTEGER NOT NULL DEFAULT 1,
                "MediaSeriesId" TEXT NULL REFERENCES "MediaSeries" ("Id") ON DELETE SET NULL,
                "JitenDeckId" INTEGER NULL, "JitenSubdeckId" INTEGER NULL,
                "CoverUrl" TEXT NULL, "CoverSource" INTEGER NOT NULL DEFAULT 0,
                "CoverProviderItemId" TEXT NULL, "JitenCharacterCount" INTEGER NULL,
                "TtsuCharacterCount" INTEGER NULL, "ManualCharacterCountOverride" INTEGER NULL,
                "IsCompleted" INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE "TtsuBindings" (
                "MediaWorkId" TEXT NOT NULL PRIMARY KEY REFERENCES "MediaWorks" ("Id") ON DELETE CASCADE,
                "OriginalTitle" TEXT NOT NULL, "FolderHint" TEXT NULL, "Version" TEXT NOT NULL,
                "CurrentCharacterPosition" INTEGER NULL, "ProgressFraction" REAL NULL,
                "ProgressRevision" INTEGER NULL, "ProgressExporterVersion" INTEGER NULL,
                "ProgressDatabaseVersion" INTEGER NULL, "TotalInferenceKind" INTEGER NULL);
            CREATE TABLE "TtsuImportReceipts" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Books" INTEGER NOT NULL,
                "AddedDays" INTEGER NOT NULL, "UpdatedDays" INTEGER NOT NULL,
                "UnchangedDays" INTEGER NOT NULL, "StaleDays" INTEGER NOT NULL,
                "ProgressUpdates" INTEGER NOT NULL, "CharacterTotalUpdates" INTEGER NOT NULL,
                "MetadataLinks" INTEGER NOT NULL, "MetadataSkips" INTEGER NOT NULL);
            CREATE TABLE "ImmersionLogs" (
                "Id" TEXT NOT NULL PRIMARY KEY, "Date" TEXT NOT NULL,
                "CharactersRead" INTEGER NOT NULL, "TimeSpentMinutes" REAL NOT NULL,
                "Source" TEXT NOT NULL, "MediaWorkId" TEXT NULL REFERENCES "MediaWorks" ("Id"),
                "TtsuBindingId" TEXT NULL REFERENCES "TtsuBindings" ("MediaWorkId") ON DELETE RESTRICT,
                "SourceRevision" INTEGER NULL);
            """);

        await ExecuteAsync(connection,
            "INSERT INTO \"MediaSeries\" (\"Id\", \"Title\", \"MediaType\") VALUES ($id, 'Series', 1);",
            ("$id", fixture.SeriesId));
        await InsertWorkAsync(connection, fixture.StandaloneId, "Standalone", null, null, null,
            "https://example.com/manual.jpg", 4, null, 123_456, 0);
        await InsertWorkAsync(connection, fixture.UniqueDeckId, "Deck", fixture.SeriesId, 30, null,
            "https://cdn.jiten.moe/deck.jpg", 2, 80_000, null, null);
        await InsertWorkAsync(connection, fixture.UniqueChildId, "Child", fixture.SeriesId, 10, 11,
            "https://cdn.jiten.moe/parent.jpg", 3, 90_000, null, null);
        await InsertWorkAsync(connection, fixture.DuplicateOneId, "Duplicate A", fixture.SeriesId, 20, 21,
            null, 0, 70_000, null, null);
        await InsertWorkAsync(connection, fixture.DuplicateTwoId, "Duplicate B", fixture.SeriesId, 20, 21,
            null, 0, 75_000, null, null);
        await InsertWorkAsync(connection, fixture.MismatchedTypeId, "Legacy anime in book series", fixture.SeriesId,
            null, null, null, 0, null, null, null, mediaType: 2);

        await ExecuteAsync(connection, """
            INSERT INTO "TtsuBindings" (
                "MediaWorkId", "OriginalTitle", "FolderHint", "Version",
                "CurrentCharacterPosition", "ProgressFraction", "ProgressRevision",
                "ProgressExporterVersion", "ProgressDatabaseVersion", "TotalInferenceKind")
            VALUES ($work, 'STANDALONE', 'folder', $version, 500, 0.5, 99, 2, 3, 1);
            """, ("$work", fixture.StandaloneId), ("$version", Guid.NewGuid()));
        await ExecuteAsync(connection, """
            INSERT INTO "TtsuImportReceipts" VALUES ($id, 3, 7, 2, 1, 0, 1, 1, 2, 1);
            """, ("$id", fixture.ReceiptId));
        await ExecuteAsync(connection, """
            INSERT INTO "ImmersionLogs" VALUES ($id, '2026-09-01', 500, 12.5, 'ttsu', $work, $work, 99);
            """, ("$id", fixture.LogIds[0]), ("$work", fixture.StandaloneId));
        await ExecuteAsync(connection, """
            INSERT INTO "ImmersionLogs" VALUES ($id, '2026-09-02', 100, 5.0, 'manual', $work, NULL, NULL);
            """, ("$id", fixture.LogIds[1]), ("$work", fixture.UniqueDeckId));
        await ExecuteAsync(connection, """
            INSERT INTO "ImmersionLogs" VALUES ($id, '2026-09-03', 50, 2.0, 'ttsu', NULL, NULL, 100);
            """, ("$id", fixture.LogIds[2]));
        return fixture;
    }

    private static Task InsertWorkAsync(
        SqliteConnection connection,
        Guid id,
        string title,
        Guid? seriesId,
        int? deckId,
        int? subdeckId,
        string? coverUrl,
        int coverSource,
        int? jitenCount,
        int? ttsuCount,
        int? manualCount,
        int mediaType = 1) => ExecuteAsync(connection, """
            INSERT INTO "MediaWorks" (
                "Id", "Title", "MediaType", "MediaSeriesId", "JitenDeckId", "JitenSubdeckId",
                "CoverUrl", "CoverSource", "CoverProviderItemId", "JitenCharacterCount",
                "TtsuCharacterCount", "ManualCharacterCountOverride", "IsCompleted")
            VALUES ($id, $title, $mediaType, $series, $deck, $subdeck, $cover, $source, NULL, $jiten, $ttsu, $manual, 0);
            """,
            ("$id", id), ("$title", title), ("$series", seriesId), ("$deck", deckId),
            ("$subdeck", subdeckId), ("$cover", coverUrl), ("$source", coverSource),
            ("$jiten", jitenCount), ("$ttsu", ttsuCount), ("$manual", manualCount),
            ("$mediaType", mediaType));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private sealed record LegacyFixture(
        Guid SeriesId,
        Guid StandaloneId,
        Guid UniqueDeckId,
        Guid UniqueChildId,
        Guid DuplicateOneId,
        Guid DuplicateTwoId,
        Guid MismatchedTypeId,
        Guid ReceiptId,
        IReadOnlyList<Guid> LogIds);

    private sealed class ThrowBeforeIndexesInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ImmersionLogs_TtsuBindingId_Date\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated interrupted SQLite upgrade.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
