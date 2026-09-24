using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class SeriesProgressCalculatorTests
{
    [Fact]
    public void FourReleasedVolumes_ReportCompletionTrackingAndCharacterProgressIndependently()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000,
                [new CopyProgressInput(Guid.NewGuid(), true, 100_000, 100_000, 60)]),
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000,
                [new CopyProgressInput(Guid.NewGuid(), false, 100_000, 50_000, 30)]),
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000, []),
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000, [])
        ]));

        Assert.Equal(4, result.ReleasedIncludedInstallmentCount);
        Assert.Equal(1, result.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(2, result.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(2, result.UntrackedReleasedIncludedInstallmentCount);
        Assert.Equal(37.5d, result.ProgressPercentage);
        Assert.False(result.IsSeriesComplete);
    }

    [Fact]
    public void CatalogueOnlyReleasedInstallment_IsKnownZeroProgressAndCountsOnce()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000, [])
        ]));

        Assert.Equal(0d, result.ProgressPercentage);
        Assert.Equal(1, result.UntrackedEligibleInstallmentCount);
        Assert.Equal(1, result.KnownCanonicalTotalInstallmentCount);
        Assert.Equal(100_000, result.ComputableProgressCharacterCount);
    }

    [Fact]
    public void MultipleCopies_UseGreatestEditionFractionButSumLifetimeActivitySeparately()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 10_000,
            [
                new CopyProgressInput(Guid.NewGuid(), false, 1_000, 100, 10),
                new CopyProgressInput(Guid.NewGuid(), false, 2_000, 1_000, 20)
            ])
        ]));

        Assert.Equal(50d, result.ProgressPercentage);
        Assert.Equal(1, result.ComputableProgressInstallmentCount);
        Assert.Equal(1_100, result.LifetimeCharactersRead);
        Assert.Equal(30d, result.LifetimeMinutes);
    }

    [Fact]
    public void MultipleCopies_ExplicitCompletionCompletesTheInstallmentOnlyOnce()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000,
            [
                new CopyProgressInput(Guid.NewGuid(), false, 100_000, 25_000, 10),
                new CopyProgressInput(Guid.NewGuid(), true, null, 0, 0)
            ])
        ]));

        Assert.Equal(1, result.ReleasedIncludedInstallmentCount);
        Assert.Equal(1, result.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(1, result.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(0, result.UntrackedReleasedIncludedInstallmentCount);
        Assert.Equal(100d, result.ProgressPercentage);
        Assert.True(result.IsSeriesComplete);
    }

    [Fact]
    public void ManualCompletionWithoutEditionTotal_CompletesInstallmentWithoutInventingLifetimeCharacters()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 80_000,
            [new CopyProgressInput(Guid.NewGuid(), true, null, 0, 0)])
        ]));

        Assert.Equal(100d, result.ProgressPercentage);
        Assert.Equal(0, result.LifetimeCharactersRead);
    }

    [Fact]
    public void ExplicitZeroEditionTotal_IsUnknownProgressNotFalseZero()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 50_000,
            [new CopyProgressInput(Guid.NewGuid(), false, null, 100, 10)])
        ]));

        Assert.Null(result.ProgressPercentage);
        Assert.Equal(1, result.UnknownProgressInstallmentCount);
        Assert.Equal(50_000, result.UnknownProgressCharacterCount);
    }

    [Fact]
    public void MissingCanonicalTotal_DoesNotRemoveACompletedVolumeFromCompletionCounts()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Released, 100_000,
                [new CopyProgressInput(Guid.NewGuid(), false, null, 1_000, 10)]),
            Installment(Guid.NewGuid(), ReleaseState.Released, null,
                [new CopyProgressInput(Guid.NewGuid(), true, null, 0, 0)])
        ]));

        Assert.Equal(2, result.ReleasedIncludedInstallmentCount);
        Assert.Equal(1, result.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(2, result.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(1, result.KnownCanonicalTotalInstallmentCount);
        Assert.Equal(1, result.UnknownCanonicalTotalInstallmentCount);
        Assert.Equal(1, result.UnknownProgressInstallmentCount);
        Assert.Null(result.ProgressPercentage);
    }

    [Fact]
    public void UnknownUpcomingAndExcludedInstallments_AreVisibleButOutsideHeadline()
    {
        var result = SeriesProgressCalculator.Calculate(new SeriesProgressInput(Guid.NewGuid(), MediaType.Book,
        [
            Installment(Guid.NewGuid(), ReleaseState.Unknown, 10_000, []),
            Installment(Guid.NewGuid(), ReleaseState.Upcoming, 20_000, []),
            new InstallmentProgressInput(Guid.NewGuid(), false, ReleaseState.Released, 30_000, [])
        ]));

        Assert.Null(result.ProgressPercentage);
        Assert.Equal(1, result.UnknownReleaseInstallmentCount);
        Assert.Equal(1, result.UpcomingInstallmentCount);
        Assert.Equal(1, result.ExcludedInstallmentCount);
        Assert.Equal(0, result.EligibleInstallmentCount);
        Assert.Equal(0, result.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(0, result.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(0, result.UntrackedReleasedIncludedInstallmentCount);
        Assert.False(result.IsSeriesComplete);
    }

    private static InstallmentProgressInput Installment(Guid id, ReleaseState release, int? total,
        IReadOnlyList<CopyProgressInput> copies) => new(id, true, release, total, copies);
}

public sealed class SeriesCatalogueServiceTests
{
    [Fact]
    public async Task ManualCatalogueCommands_PersistOrderAndCatalogueOnlyEntriesWithoutCreatingLibraryWorks()
    {
        await using var database = await Database.CreateAsync();
        var commands = new SeriesCatalogueService(database.Context);
        var series = await commands.CreateSeriesAsync(new CreateSeriesCommand("Main novels", MediaType.Book));
        var first = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Volume 1",
            InstallmentKind.Volume, ReleaseState.Released, null, CanonicalCharacterCount: 100_000));
        var second = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Volume 2",
            InstallmentKind.Volume, ReleaseState.Upcoming, null));
        await commands.ReorderInstallmentsAsync(new ReorderInstallmentsCommand(series.Id, [second.Id, first.Id],
            new Dictionary<Guid, Guid> { [first.Id] = first.Version, [second.Id] = second.Version }));
        await commands.EditInstallmentAsync(new EditInstallmentCommand(series.Id, first.Id, first.Version, "Volume One",
            InstallmentKind.Volume, ReleaseState.Released, null, true, 110_000));

        database.Context.ChangeTracker.Clear();
        var details = await new SeriesCatalogueQueryService(database.Context).GetDetailsAsync(series.Id);
        Assert.NotNull(details);
        Assert.Equal(["Volume 2", "Volume One"], details.Installments.Select(item => item.Title));
        Assert.Equal(110_000, details.Installments[1].CanonicalCharacterCount);
        Assert.Empty(await database.Context.MediaWorks.AsNoTracking().ToListAsync());
        Assert.Equal(0d, details.Progress.ProgressPercentage);
        Assert.Equal(1, details.Progress.UntrackedEligibleInstallmentCount);
    }

    [Fact]
    public async Task AssociationAndReviewedConsolidation_MoveCopiesWithoutMergingTheirHistory()
    {
        await using var database = await Database.CreateAsync();
        var commands = new SeriesCatalogueService(database.Context);
        var series = await commands.CreateSeriesAsync(new CreateSeriesCommand("Series", MediaType.Book));
        var source = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Volume 1 ebook",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var target = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Volume 1 canonical",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var copy = await commands.CreateCopyAsync(new CreateCopyForInstallmentCommand(series.Id, source.Id, "My ebook"));
        copy.LinkToJitenDeck(42, 90_000);
        source.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "deck:42",
            ProviderItemId = 42
        });
        var log = new ImmersionLog { MediaWorkId = copy.Id, Date = new DateOnly(2026, 9, 22), CharactersRead = 345, TimeSpentMinutes = 12 };
        database.Context.ImmersionLogs.Add(log);
        await database.Context.SaveChangesAsync();
        var logId = log.Id;

        await commands.ConsolidateInstallmentsAsync(new ReviewedCanonicalConsolidationCommand(
            series.Id, source.Id, source.Version, target.Id, target.Version, [copy.Id]));
        database.Context.ChangeTracker.Clear();

        var moved = await database.Context.MediaWorks.AsNoTracking().SingleAsync();
        Assert.Equal(target.Id, moved.MediaInstallmentId);
        Assert.Equal(series.Id, moved.MediaSeriesId);
        Assert.Equal(logId, await database.Context.ImmersionLogs.AsNoTracking().Select(item => item.Id).SingleAsync());
        Assert.Equal(345, await database.Context.ImmersionLogs.AsNoTracking().Select(item => item.CharactersRead).SingleAsync());
        Assert.NotNull(await database.Context.MediaInstallments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == source.Id));
        Assert.Equal(target.Id, await database.Context.InstallmentProviderIdentities.AsNoTracking()
            .Where(item => item.Provider == "jiten" && item.NormalizedKey == "deck:42")
            .Select(item => item.MediaInstallmentId).SingleAsync());
    }

    [Fact]
    public async Task Query_UsesCompatibleCanonicalMetadataAndReportsCoverageWithoutLoadingLogs()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var tracked = new MediaInstallment("Tracked", MediaType.Book)
        {
            MediaSeries = series, TitleOverride = "Tracked", ReleaseState = ReleaseState.Released,
            CanonicalCharacterCount = 100_000,
            CanonicalCoverUrl = "https://cdn.jiten.moe/tracked.jpg",
            CanonicalCoverSource = CanonicalCoverSource.ProviderExact
        };
        var unknown = new MediaInstallment("Unknown", MediaType.Book)
        {
            MediaSeries = series, ReleaseState = ReleaseState.Released
        };
        var copy = new MediaWork("Edition") { MediaInstallment = tracked, MediaSeries = series };
        copy.LinkToJitenDeck(10, 60_000);
        tracked.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten", NormalizedKey = "deck:10", ProviderItemId = 10
        });
        copy.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 22), CharactersRead = 30_000, TimeSpentMinutes = 25 });
        database.Context.AddRange(tracked, unknown, copy);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var index = await new SeriesCatalogueQueryService(database.Context).GetIndexAsync();
        var item = Assert.Single(index.Items);
        Assert.Equal(30d, item.Progress.ProgressPercentage);
        Assert.Equal(1, item.Progress.KnownCanonicalTotalInstallmentCount);
        Assert.Equal(1, item.Progress.UnknownCanonicalTotalInstallmentCount);
        Assert.Equal(30_000, item.Progress.LifetimeCharactersRead);
        Assert.Equal(25d, item.Progress.LifetimeMinutes);
        Assert.Equal("https://cdn.jiten.moe/tracked.jpg", item.Cover?.Url);
        Assert.Equal(CatalogueCoverOrigin.CanonicalProviderExact, item.Cover?.Origin);

        var details = await new SeriesCatalogueQueryService(database.Context).GetDetailsAsync(series.Id);
        Assert.Equal("https://cdn.jiten.moe/tracked.jpg",
            details!.Installments.Single(installment => installment.Id == tracked.Id).Cover?.Url);
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DetailsSummary_UsesTheWholeSeriesBeyondItsDisplayedRowLimit_AndMatchesIndex()
    {
        await using var database = await Database.CreateAsync();
        var series = new MediaSeries("Long series", MediaType.Book);
        var installments = Enumerable.Range(1, 501).Select(number => new MediaInstallment($"Volume {number}", MediaType.Book)
        {
            MediaSeries = series,
            OrderKey = number * 100,
            ReleaseState = ReleaseState.Released,
            CanonicalCharacterCount = 100_000
        }).ToList();
        var completedCopy = new MediaWork("Completed edition")
        {
            MediaInstallment = installments[^1],
            MediaSeries = series,
            IsCompleted = true
        };
        database.Context.AddRange(installments);
        database.Context.Add(completedCopy);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var queries = new SeriesCatalogueQueryService(database.Context);
        var indexItem = Assert.Single((await queries.GetIndexAsync()).Items);
        var details = Assert.IsType<SeriesCatalogueDetails>(await queries.GetDetailsAsync(series.Id));

        Assert.True(details.IsTruncated);
        Assert.Equal(500, details.Installments.Count);
        Assert.Equal(501, details.Progress.ReleasedIncludedInstallmentCount);
        Assert.Equal(1, details.Progress.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(1, details.Progress.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(500, details.Progress.UntrackedReleasedIncludedInstallmentCount);
        Assert.Equal(indexItem.Progress.ReleasedIncludedInstallmentCount,
            details.Progress.ReleasedIncludedInstallmentCount);
        Assert.Equal(indexItem.Progress.CompletedReleasedIncludedInstallmentCount,
            details.Progress.CompletedReleasedIncludedInstallmentCount);
        Assert.Equal(indexItem.Progress.TrackedReleasedIncludedInstallmentCount,
            details.Progress.TrackedReleasedIncludedInstallmentCount);
        Assert.Equal(indexItem.Progress.ProgressPercentage, details.Progress.ProgressPercentage);
        Assert.Empty(database.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Commands_RejectCrossSeriesTargetsAndDirectMovesOfAssignedCopies()
    {
        await using var database = await Database.CreateAsync();
        var commands = new SeriesCatalogueService(database.Context);
        var firstSeries = await commands.CreateSeriesAsync(new CreateSeriesCommand("First", MediaType.Book));
        var secondSeries = await commands.CreateSeriesAsync(new CreateSeriesCommand("Second", MediaType.Book));
        var first = await commands.AddInstallmentAsync(new AddInstallmentCommand(firstSeries.Id, "First volume",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var second = await commands.AddInstallmentAsync(new AddInstallmentCommand(secondSeries.Id, "Second volume",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var copy = await commands.CreateCopyAsync(new CreateCopyForInstallmentCommand(firstSeries.Id, first.Id, "Copy"));

        await Assert.ThrowsAsync<MediaCatalogConflictException>(() => commands.EditInstallmentAsync(
            new EditInstallmentCommand(firstSeries.Id, second.Id, second.Version, "Tampered",
                InstallmentKind.Volume, null, null, true, null)));
        await Assert.ThrowsAsync<MediaCatalogConflictException>(() => commands.CreateCopyAsync(
            new CreateCopyForInstallmentCommand(firstSeries.Id, second.Id, "Cross-series copy")));
        await Assert.ThrowsAsync<MediaCatalogConflictException>(() => commands.AssociateCopyAsync(
            new AssociateCopyCommand(secondSeries.Id, copy.Id, copy.Version, first.Id, first.Version,
                second.Id, second.Version)));

        database.Context.ChangeTracker.Clear();
        Assert.Equal(first.Id, (await database.Context.MediaWorks.AsNoTracking().SingleAsync()).MediaInstallmentId);
        Assert.Equal("Second volume", (await database.Context.MediaInstallments.AsNoTracking()
            .SingleAsync(item => item.Id == second.Id)).LegacyTitle);
    }

    [Fact]
    public async Task AssociateCopy_MovesAReviewedStandaloneCopyAndItsProviderIdentity()
    {
        await using var database = await Database.CreateAsync();
        var commands = new SeriesCatalogueService(database.Context);
        var series = await commands.CreateSeriesAsync(new CreateSeriesCommand("Series", MediaType.Book));
        var target = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Volume",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var standalone = new MediaInstallment("Standalone", MediaType.Book);
        var copy = new MediaWork("Edition", jitenDeckId: 42) { MediaInstallment = standalone };
        copy.Logs.Add(new ImmersionLog
            { Date = new DateOnly(2026, 9, 22), CharactersRead = 1234, TimeSpentMinutes = 12 });
        standalone.ProviderIdentities.Add(new InstallmentProviderIdentity
            { Provider = "jiten", NormalizedKey = "deck:42", ProviderItemId = 42 });
        database.Context.AddRange(standalone, copy);
        await database.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<MediaCatalogConflictException>(() => commands.AssociateCopyAsync(
            new AssociateCopyCommand(series.Id, copy.Id, copy.Version, standalone.Id, standalone.Version,
                target.Id, Guid.NewGuid())));
        await commands.AssociateCopyAsync(new AssociateCopyCommand(
            series.Id, copy.Id, copy.Version, standalone.Id, standalone.Version, target.Id, target.Version));

        database.Context.ChangeTracker.Clear();
        var persistedCopy = await database.Context.MediaWorks.Include(item => item.Logs).AsNoTracking()
            .SingleAsync(item => item.Id == copy.Id);
        Assert.Equal(target.Id, persistedCopy.MediaInstallmentId);
        Assert.Equal(series.Id, persistedCopy.MediaSeriesId);
        Assert.Single(persistedCopy.Logs);
        var identity = await database.Context.InstallmentProviderIdentities.AsNoTracking().SingleAsync();
        Assert.Equal(target.Id, identity.MediaInstallmentId);
        Assert.NotNull(await database.Context.MediaInstallments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == standalone.Id));
    }

    [Fact]
    public async Task Reorder_RejectsAStaleInstallmentSnapshot()
    {
        await using var database = await Database.CreateAsync();
        var commands = new SeriesCatalogueService(database.Context);
        var series = await commands.CreateSeriesAsync(new CreateSeriesCommand("Series", MediaType.Book));
        var first = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "First",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var second = await commands.AddInstallmentAsync(new AddInstallmentCommand(series.Id, "Second",
            InstallmentKind.Volume, ReleaseState.Released, null));
        var staleVersions = new Dictionary<Guid, Guid> { [first.Id] = first.Version, [second.Id] = second.Version };
        second.Version = Guid.NewGuid();
        await database.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<MediaCatalogConflictException>(() => commands.ReorderInstallmentsAsync(
            new ReorderInstallmentsCommand(series.Id, [second.Id, first.Id], staleVersions)));
    }

    private sealed class Database(SqliteConnection connection, ImmersionDbContext context) : IAsyncDisposable
    {
        public ImmersionDbContext Context { get; } = context;

        public static async Task<Database> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ImmersionDbContext(new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Database(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
