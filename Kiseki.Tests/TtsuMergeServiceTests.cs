using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class TtsuMergeServiceTests
{
    [Theory]
    [InlineData(3, 50, TtsuDayAction.Updated, 50)]
    [InlineData(3, 0, TtsuDayAction.Updated, 0)]
    [InlineData(1, 500, TtsuDayAction.Stale, 100)]
    [InlineData(2, 100, TtsuDayAction.Unchanged, 100)]
    [InlineData(2, 500, TtsuDayAction.Conflict, 100)]
    [InlineData(0, 500, TtsuDayAction.Conflict, 100)]
    public void Planner_OrdersDailyRevisions(long revision, int characters, TtsuDayAction action, int result)
    {
        var work = TtsuBookImporter.CreateMediaWork(Book(Entry(100, 2)));
        var plan = TtsuMergePlanner.Plan(work, Book(Entry(characters, revision)));
        Assert.Equal(action, Assert.Single(plan.Days).Action);
        Assert.Equal(result, plan.ResultCharacters);
    }

    [Fact]
    public async Task Reimport_IsIdempotentAndPreservesMetadataOtherSourcesAndMissingDays()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = TtsuBookImporter.CreateMediaWork(Book(Entry(100, 1), Entry(20, 1, "2026-09-02")));
        work.LinkToJitenDeck(10, 1000, "https://example.com/cover.jpg");
        work.IsCompleted = true;
        work.ManualCharacterCountOverride = 900;
        work.Logs.Add(new() { Date = new(2026, 9, 1), CharactersRead = 30, Source = "manual" });
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        var ids = work.Logs.Select(x => x.Id).Order().ToArray();
        var incoming = Book(Entry(200, 2), Entry(70, 2, "2026-09-03"));
        await Apply(db.Service, incoming, work.Id);
        var repeated = await Apply(db.Service, incoming, work.Id);
        Assert.Equal(0, repeated.AddedDays);
        Assert.Equal(0, repeated.UpdatedDays);
        db.Context.ChangeTracker.Clear();
        work = await db.Context.MediaWorks.Include(x => x.Logs).SingleAsync();
        Assert.Equal(320, work.CurrentCharactersRead);
        Assert.All(ids, id => Assert.Contains(work.Logs, x => x.Id == id));
        Assert.Equal(4, work.Logs.Count);
        Assert.True(work.IsCompleted);
        Assert.Equal(900, work.ManualCharacterCountOverride);
        Assert.Equal(10, work.JitenDeckId);
    }

    [Fact]
    public async Task LegacyBaseline_RequiresReviewAndAdoptsInPlace()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Book");
        var log = new ImmersionLog { Date = new(2026, 9, 1), CharactersRead = 90, TimeSpentMinutes = 1 };
        work.Logs.Add(log);
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        var incoming = Book(Entry(100, 2));
        var preview = await db.Service.PreviewAsync(incoming, work.Id);
        Assert.False(preview.CanApply);
        await Assert.ThrowsAsync<TtsuImportReviewRequiredException>(() => db.Service.ApplyAsync(Guid.NewGuid(), [new(incoming, work.Id, new Dictionary<DateOnly, string>(), preview.Fingerprint)]));
        await Apply(db.Service, incoming, work.Id, new() { [log.Date] = "incoming:0" });
        db.Context.ChangeTracker.Clear();
        log = await db.Context.ImmersionLogs.SingleAsync();
        Assert.Equal(100, log.CharactersRead);
        Assert.Equal(2, log.SourceRevision);
        Assert.Equal(work.Id, log.TtsuBindingId);
    }

    [Fact]
    public async Task KeepingLegacyValue_DoesNotAcceptIncomingRevision()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Book");
        var log = new ImmersionLog { Date = new(2026, 9, 1), CharactersRead = 90 };
        work.Logs.Add(log);
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        await Apply(db.Service, Book(Entry(100, 2)), work.Id, new() { [log.Date] = $"keep:{log.Id}" });
        db.Context.ChangeTracker.Clear();
        Assert.Null((await db.Context.ImmersionLogs.SingleAsync()).SourceRevision);
        Assert.False((await db.Service.PreviewAsync(Book(Entry(100, 3)), work.Id)).CanApply);
    }

    [Fact]
    public async Task DuplicateLegacyRows_ResolveExplicitlyIncludingDatesAbsentFromUpload()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Book");
        var first = new ImmersionLog { Date = new(2026, 9, 1), CharactersRead = 10 };
        var keep = new ImmersionLog { Date = first.Date, CharactersRead = 25 };
        work.Logs.AddRange([first, keep]);
        db.Context.Add(work);
        await db.Context.SaveChangesAsync();
        Assert.False((await db.Service.PreviewAsync(Book(), work.Id)).CanApply);
        await Apply(db.Service, Book(), work.Id, new() { [first.Date] = $"keep:{keep.Id}" });
        db.Context.ChangeTracker.Clear();
        Assert.Equal(keep.Id, (await db.Context.ImmersionLogs.SingleAsync()).Id);
        Assert.Equal(25, (await db.Context.ImmersionLogs.SingleAsync()).CharactersRead);
    }

    [Fact]
    public async Task OrphanAssignment_RequiresExplicitTargetAndPreservesLogId()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var work = new MediaWork("Book");
        var orphan = new ImmersionLog { Date = new(2026, 9, 1), CharactersRead = 10 };
        db.Context.AddRange(work, orphan);
        await db.Context.SaveChangesAsync();
        var preview = await db.Service.PreviewAsync(Book(), work.Id, orphanLogIds: [orphan.Id]);
        await db.Service.ApplyAsync(Guid.NewGuid(), [new(Book(), work.Id, new Dictionary<DateOnly, string>(), preview.Fingerprint, [orphan.Id])]);
        db.Context.ChangeTracker.Clear();
        Assert.Equal(work.Id, (await db.Context.ImmersionLogs.SingleAsync()).MediaWorkId);
        Assert.Equal(orphan.Id, (await db.Context.ImmersionLogs.SingleAsync()).Id);
    }

    [Fact]
    public async Task Rename_UsesBindingAndDuplicateSourcesRequireSelection()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var book = Book(Entry(100, 1));
        await Apply(db.Service, book);
        var work = await db.Context.MediaWorks.SingleAsync();
        work.Title = "A Jiten title";
        await db.Context.SaveChangesAsync();
        Assert.Equal(work.Id, (await db.Service.MatchAsync(book)).WorkId);
        await Apply(db.Service, book); // Explicit copy.
        Assert.Null((await db.Service.MatchAsync(book)).WorkId);
        Assert.Contains("Multiple", (await db.Service.MatchAsync(book)).Reason);
    }

    [Fact]
    public async Task StalePreview_RollsBackEntireBatchAndDeletedTargetsDoNotBecomeCopies()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var book = Book(Entry(100, 1));
        await Apply(db.Service, book);
        var work = await db.Context.MediaWorks.Include(x => x.Logs).SingleAsync();
        var update = Book(Entry(200, 2));
        var oldPlan = await db.Service.PreviewAsync(update, work.Id);
        work.Logs.Single().CharactersRead = 101;
        await db.Context.SaveChangesAsync();
        var other = Book(Entry(30, 1)); other.Title = "Other"; other.Entries.ForEach(x => x.Title = "Other");
        var newPlan = await db.Service.PreviewAsync(other, null);
        var operation = Guid.NewGuid();
        await Assert.ThrowsAsync<TtsuImportReviewRequiredException>(() => db.Service.ApplyAsync(operation,
            [new(other, null, new Dictionary<DateOnly, string>(), newPlan.Fingerprint), new(update, work.Id, new Dictionary<DateOnly, string>(), oldPlan.Fingerprint)]));
        db.Context.ChangeTracker.Clear();
        Assert.Single(await db.Context.MediaWorks.ToListAsync());
        Assert.Null(await db.Service.GetReceiptAsync(operation));
        Assert.False((await db.Service.PreviewAsync(book, Guid.NewGuid())).CanApply);
    }

    [Fact]
    public async Task Receipt_ReplayAfterCacheLossDoesNotCreateAnotherCopy()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var book = Book(Entry(100, 1));
        var preview = await db.Service.PreviewAsync(book, null);
        var operation = Guid.NewGuid();
        TtsuImportRequest[] requests = [new(book, null, new Dictionary<DateOnly, string>(), preview.Fingerprint)];
        await db.Service.ApplyAsync(operation, requests);
        await db.Service.ApplyAsync(operation, requests);
        Assert.Single(await db.Context.MediaWorks.ToListAsync());
        Assert.Single(await db.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnlyRevisionAdvance_ProtectsAgainstIntermediateOlderExports()
    {
        await using var db = await ImportDatabase.CreateAsync();
        await Apply(db.Service, Book(Entry(100, 1)));
        var id = (await db.Context.MediaWorks.SingleAsync()).Id;
        var result = await Apply(db.Service, Book(Entry(100, 3)), id);
        Assert.Equal(0, result.UpdatedDays);
        Assert.Equal(TtsuDayAction.Stale, Assert.Single((await db.Service.PreviewAsync(Book(Entry(500, 2)), id)).Days).Action);
    }

    [Fact]
    public async Task ProgressImport_StoresPositionAndUsesInferredTtsuTotal()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var book = Book(Entry(10_000, 1));
        book.ProgressEntries.Add(Progress(25_000, 0.25, 10));

        var receipt = await Apply(db.Service, book);

        db.Context.ChangeTracker.Clear();
        var work = await db.Context.MediaWorks.SingleAsync();
        var binding = await db.Context.TtsuBindings.SingleAsync();
        Assert.Equal(100_000, work.TtsuCharacterCount);
        Assert.Equal(100_000, work.TotalCharacters);
        Assert.Equal(25_000, binding.CurrentCharacterPosition);
        Assert.Equal(0.25, binding.ProgressFraction);
        Assert.Equal(10, binding.ProgressRevision);
        Assert.Equal(1, receipt.ProgressUpdates);
        Assert.Equal(1, receipt.CharacterTotalUpdates);

        var repeated = await Apply(db.Service, book, work.Id);
        Assert.Equal(0, repeated.ProgressUpdates);
        Assert.Equal(0, repeated.CharacterTotalUpdates);
    }

    [Fact]
    public async Task ProgressImport_SkipsOlderBookmarkAndManualTotalKeepsPriority()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var first = Book();
        first.ProgressEntries.Add(Progress(25_000, 0.25, 10));
        await Apply(db.Service, first);
        var work = await db.Context.MediaWorks.SingleAsync();
        work.ManualCharacterCountOverride = 90_000;
        await db.Context.SaveChangesAsync();

        var stale = Book();
        stale.ProgressEntries.Add(Progress(40_000, 0.2, 9));
        var preview = await db.Service.PreviewAsync(stale, work.Id);
        var receipt = await db.Service.ApplyAsync(Guid.NewGuid(),
            [new(stale, work.Id, new Dictionary<DateOnly, string>(), preview.Fingerprint)]);

        db.Context.ChangeTracker.Clear();
        work = await db.Context.MediaWorks.SingleAsync();
        var binding = await db.Context.TtsuBindings.SingleAsync();
        Assert.Equal(TtsuProgressAction.Stale, preview.Progress.Action);
        Assert.Equal(25_000, binding.CurrentCharacterPosition);
        Assert.Equal(100_000, work.TtsuCharacterCount);
        Assert.Equal(90_000, work.TotalCharacters);
        Assert.Equal(0, receipt.ProgressUpdates);
    }

    [Fact]
    public async Task ProgressImport_SameRevisionConflictRequiresExplicitChoice()
    {
        await using var db = await ImportDatabase.CreateAsync();
        var baseline = Book();
        baseline.ProgressEntries.Add(Progress(25_000, 0.25, 10));
        await Apply(db.Service, baseline);
        var workId = (await db.Context.MediaWorks.SingleAsync()).Id;

        var conflicting = Book();
        conflicting.ProgressEntries.Add(Progress(30_000, 0.3, 10));
        var unresolved = await db.Service.PreviewAsync(conflicting, workId);

        Assert.False(unresolved.CanApply);
        Assert.Equal(TtsuProgressAction.Conflict, unresolved.Progress.Action);

        var reviewed = await db.Service.PreviewAsync(
            conflicting,
            workId,
            progressResolution: "incoming:0");
        await db.Service.ApplyAsync(Guid.NewGuid(),
            [new(conflicting, workId, new Dictionary<DateOnly, string>(), reviewed.Fingerprint,
                ProgressResolution: "incoming:0")]);

        db.Context.ChangeTracker.Clear();
        var binding = await db.Context.TtsuBindings.SingleAsync();
        Assert.Equal(30_000, binding.CurrentCharacterPosition);
        Assert.Equal(0.3, binding.ProgressFraction);
    }

    [Fact]
    public void MultipleFiles_UnionDailyRevisionsAndExposeConflictingTies()
    {
        var first = Book(Entry(10, 1), Entry(20, 4, "2026-09-02"));
        var second = Book(Entry(30, 3), Entry(99, 2, "2026-09-02"), Entry(50, 1, "2026-09-03"));
        var combined = Assert.Single(TtsuStatisticsNormalizer.CombineFiles([first, second]));
        var plan = TtsuMergePlanner.Plan(null, combined);
        Assert.Equal(100, plan.ResultCharacters);
        Assert.Equal(3, plan.Days.Count);
        combined.Entries.Add(Entry(31, 3));
        Assert.False(TtsuMergePlanner.Plan(null, combined).CanApply);
        second.FolderHint = "Another folder";
        Assert.Equal(2, TtsuStatisticsNormalizer.CombineFiles([first, second]).Count);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"dateKey\":\"2026-09-01\",\"lastStatisticModified\":-1}]")]
    [InlineData("[{\"title\":\"A\",\"dateKey\":\"2026-09-01\"},{\"title\":\"B\",\"dateKey\":\"2026-09-01\"}]")]
    public async Task InvalidEntries_AreRejected(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TtsuDataLoader().ParseStatisticsAsync(stream, "Book"));
    }

    internal static TtsuBookContainer Book(params TtsuReaderDTO[] entries) => new() { Title = "Book", FolderHint = "Book", Entries = [.. entries] };
    internal static TtsuReaderDTO Entry(int characters, long? revision, string date = "2026-09-01") =>
        new() { Title = "Book", DateKey = date, CharactersRead = characters, ReadingTime = 60, LastStatisticModified = revision };
    internal static TtsuProgressDTO Progress(int position, double fraction, long? revision) => new()
    {
        ExploredCharacterCount = position,
        Progress = System.Text.Json.JsonSerializer.SerializeToElement(fraction),
        LastBookmarkModified = revision,
        ExporterVersion = 1,
        DatabaseVersion = 6
    };
    internal static async Task<TtsuImportReceipt> Apply(TtsuImportService service, TtsuBookContainer book, Guid? id = null, Dictionary<DateOnly, string>? resolutions = null)
    {
        resolutions ??= [];
        var plan = await service.PreviewAsync(book, id, resolutions);
        return await service.ApplyAsync(Guid.NewGuid(), [new(book, id, resolutions, plan.Fingerprint)]);
    }
}

internal sealed class ImportDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public ImmersionDbContext Context { get; }
    public TtsuImportService Service { get; }
    private ImportDatabase(SqliteConnection connection)
    {
        _connection = connection;
        Context = new(new DbContextOptionsBuilder<ImmersionDbContext>().UseSqlite(connection).Options);
        Service = new(Context);
    }
    public static async Task<ImportDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new ImportDatabase(connection);
        await database.Context.Database.EnsureCreatedAsync();
        return database;
    }
    public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await _connection.DisposeAsync(); }
}
