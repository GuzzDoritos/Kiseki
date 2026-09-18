using System.Data.Common;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static Kiseki.Tests.TtsuMergeServiceTests;

namespace Kiseki.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KISEKI_TEST_POSTGRES")))
            Skip = "Set KISEKI_TEST_POSTGRES to a disposable local PostgreSQL server to run provider integration tests.";
    }
}

public sealed class TtsuPostgreSqlTests
{
    [PostgreSqlFact]
    public async Task PostgreSql_UpgradesExistingDatabaseWithoutLosingLegacyRows()
    {
        await using var db = await PostgreSqlDatabase.CreateAsync();
        await using var context = db.Context();
        await context.GetService<IMigrator>().MigrateAsync("20260911173535_AddTtsuImportState");
        var workId = Guid.NewGuid();
        var work2Id = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"IsCompleted\", \"JitenCoverUrl\") VALUES ({workId}, {"Book"}, {1}, {false}, {"https://example.com/legacy-cover.jpg"})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"MediaWorks\" (\"Id\", \"Title\", \"MediaType\", \"IsCompleted\", \"JitenCoverUrl\") VALUES ({work2Id}, {"Book 2"}, {1}, {false}, {null})");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"TtsuImportReceipts\" (\"Id\", \"Books\", \"AddedDays\", \"UpdatedDays\", \"UnchangedDays\", \"StaleDays\") VALUES ({receiptId}, {1}, {1}, {0}, {0}, {0})");
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < ids.Length; i++)
        {
            Guid? target = i == 2 ? null : workId;
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ImmersionLogs\" (\"Id\", \"Date\", \"CharactersRead\", \"TimeSpentMinutes\", \"Source\", \"MediaWorkId\") VALUES ({ids[i]}, {new DateOnly(2026, 9, 1)}, {100}, {1d}, {"ttsu"}, {target})");
        }
        await context.Database.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        var upgradedWork1 = await context.MediaWorks.AsNoTracking().SingleAsync(x => x.Id == workId);
        Assert.Equal("https://example.com/legacy-cover.jpg", upgradedWork1.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, upgradedWork1.CoverSource);
        var upgradedWork2 = await context.MediaWorks.AsNoTracking().SingleAsync(x => x.Id == work2Id);
        Assert.Null(upgradedWork2.CoverUrl);
        Assert.Equal(MediaCoverSource.None, upgradedWork2.CoverSource);
        var logs = await context.ImmersionLogs.AsNoTracking().ToListAsync();
        Assert.Equal(ids.Order(), logs.Select(x => x.Id).Order());
        Assert.All(logs, x => Assert.Null(x.SourceRevision));
        var legacyReceipt = await context.TtsuImportReceipts.AsNoTracking().SingleAsync(x => x.Id == receiptId);
        Assert.Equal(1, legacyReceipt.Books);
        Assert.Equal(1, legacyReceipt.AddedDays);
        Assert.Equal(0, legacyReceipt.MetadataLinks);
        Assert.Equal(0, legacyReceipt.MetadataSkips);
        var service = new TtsuImportService(context);
        Assert.False((await service.PreviewAsync(Book(Entry(200, 2)), workId)).CanApply);
        await Apply(service, Book(Entry(200, 2)), workId, new() { [new(2026, 9, 1)] = "incoming:0" });
        context.ChangeTracker.Clear();
        Assert.Equal(2, await context.ImmersionLogs.CountAsync());
        Assert.Equal(200, (await context.ImmersionLogs.SingleAsync(x => x.MediaWorkId == workId)).CharactersRead);
    }

    [PostgreSqlFact]
    public async Task PostgreSql_ConcurrentFirstImportsCreateOnlyOneWork()
    {
        await using var db = await PostgreSqlDatabase.CreateAsync();
        await using (var setup = db.Context()) await setup.Database.MigrateAsync();
        var barrier = new SaveBarrier();
        await using var first = db.Context(barrier);
        await using var second = db.Context(barrier);
        var firstService = new TtsuImportService(first); var secondService = new TtsuImportService(second);
        var book = Book(Entry(100, 1));
        var preview1 = await firstService.PreviewAsync(book, null);
        var preview2 = await secondService.PreviewAsync(book, null);
        async Task<bool> Run(TtsuImportService service, string fingerprint)
        {
            try { await service.ApplyAsync(Guid.NewGuid(), [new(book, null, new Dictionary<DateOnly, string>(), fingerprint)]); return true; }
            catch (TtsuImportReviewRequiredException) { return false; }
        }
        var results = await Task.WhenAll(Run(firstService, preview1.Fingerprint), Run(secondService, preview2.Fingerprint));
        Assert.Single(results, x => x);
        await using var verify = db.Context();
        Assert.Single(await verify.MediaWorks.ToListAsync());
        Assert.Single(await verify.ImmersionLogs.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task PostgreSql_ConcurrentUpdatesRequireARefreshedReview()
    {
        await using var db = await PostgreSqlDatabase.CreateAsync();
        Guid id;
        await using (var setup = db.Context())
        {
            await setup.Database.MigrateAsync();
            await Apply(new TtsuImportService(setup), Book(Entry(100, 1)));
            id = (await setup.MediaWorks.SingleAsync()).Id;
        }
        var barrier = new SaveBarrier();
        await using var first = db.Context(barrier);
        await using var second = db.Context(barrier);
        async Task<bool> Run(ImmersionDbContext context, int count, long revision)
        {
            try { await Apply(new TtsuImportService(context), Book(Entry(count, revision)), id); return true; }
            catch (TtsuImportReviewRequiredException) { return false; }
        }
        var results = await Task.WhenAll(Run(first, 200, 2), Run(second, 300, 3));
        Assert.Single(results, x => x);
        await using var verify = db.Context();
        Assert.Single(await verify.ImmersionLogs.ToListAsync());
        // A new review converges to the newest source revision regardless of who won.
        await Apply(new TtsuImportService(verify), Book(Entry(300, 3)), id);
        verify.ChangeTracker.Clear();
        Assert.Equal(300, (await verify.ImmersionLogs.SingleAsync()).CharactersRead);
    }

    [PostgreSqlFact]
    public async Task PostgreSql_LostCommitResponseReturnsReceiptOnRetry()
    {
        await using var db = await PostgreSqlDatabase.CreateAsync();
        await using (var setup = db.Context()) await setup.Database.MigrateAsync();
        var lostResponse = new LostCommitResponse();
        await using var context = db.Context(lostResponse, retry: true);
        var service = new TtsuImportService(context);
        var book = Book(Entry(100, 1));
        var plan = await service.PreviewAsync(book, null);
        var selection = CreateSelection(10, 11, "Jiten Novel", 85_000, "https://cdn.jiten.moe/cover.jpg");
        var receipt = await service.ApplyAsync(Guid.NewGuid(),
        [
            new TtsuImportRequest(
                book,
                null,
                new Dictionary<DateOnly, string>(),
                plan.Fingerprint,
                Metadata: new TtsuMetadataImportRequest(selection))
        ]);
        Assert.Equal(1, receipt.AddedDays);
        Assert.Equal(1, receipt.MetadataLinks);
        Assert.Equal(0, receipt.MetadataSkips);
        var work = Assert.Single(await context.MediaWorks.ToListAsync());
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(11, work.JitenSubdeckId);
        Assert.Single(await context.TtsuImportReceipts.ToListAsync());
        Assert.True(lostResponse.Thrown);
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _release.SetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }
    private sealed class LostCommitResponse : DbTransactionInterceptor
    {
        public bool Thrown { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!Thrown) { Thrown = true; throw new IOException("Simulated lost commit response"); }
            return Task.CompletedTask;
        }
    }
}

internal sealed class PostgreSqlDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name = "kiseki_merge_test_" + Guid.NewGuid().ToString("N");
    private PostgreSqlDatabase(string admin) => _admin = admin;
    public static async Task<PostgreSqlDatabase> CreateAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("KISEKI_TEST_POSTGRES"));
        if (connection.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new InvalidOperationException("Provider tests only create disposable databases on a local PostgreSQL server.");
        var database = new PostgreSqlDatabase(connection.ConnectionString);
        await using var admin = new NpgsqlConnection(database._admin);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database._name}\"", admin);
        await command.ExecuteNonQueryAsync();
        return database;
    }
    public ImmersionDbContext Context(IInterceptor? interceptor = null, bool retry = false)
    {
        var connection = new NpgsqlConnectionStringBuilder(_admin) { Database = _name, Pooling = false };
        var options = new DbContextOptionsBuilder<ImmersionDbContext>().UseNpgsql(connection.ConnectionString,
            x => { if (retry) x.ExecutionStrategy(deps => new NeonRetryingExecutionStrategy(deps)); });
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
    }
}
