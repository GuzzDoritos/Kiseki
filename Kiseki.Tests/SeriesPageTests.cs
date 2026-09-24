using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Pages.Series;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Tests;

public sealed class SeriesPageTests
{
    private static readonly Guid SeriesId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Volume1Id = new("00000000-0000-0000-0000-000000000011");
    private static readonly Guid Volume2Id = new("00000000-0000-0000-0000-000000000012");
    private static readonly Guid Volume3Id = new("00000000-0000-0000-0000-000000000013");
    private static readonly Guid Volume4Id = new("00000000-0000-0000-0000-000000000014");
    private static readonly Guid Volume5Id = new("00000000-0000-0000-0000-000000000015");
    private static readonly Guid SideStoryId = new("00000000-0000-0000-0000-000000000016");

    private static readonly Guid Copy1AId = new("00000000-0000-0000-0000-000000000021");
    private static readonly Guid Copy1BId = new("00000000-0000-0000-0000-000000000022");
    private static readonly Guid Copy3Id = new("00000000-0000-0000-0000-000000000023");

    [Fact]
    public async Task NorthWindNovelsFixture_CalculatesExpectedProgressCoverageAndLifetimeActivity()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedNorthWindNovelsFixtureAsync(database.Context);

        var detailsModel = CreateDetailsModel(database.Context);
        var result = await detailsModel.OnGetAsync(SeriesId, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        var viewModel = detailsModel.SeriesDetails;
        Assert.NotNull(viewModel);

        // Expected headline: (100,000 * 0.5 + 200,000 * 0) / 300,000 = 16.666...%
        Assert.NotNull(viewModel.ProgressPercentage);
        Assert.InRange(viewModel.ProgressPercentage.Value, 16.66, 16.67);
        Assert.Equal("16.7%", viewModel.HeadlineProgressText);

        var progress = viewModel.Progress;
        Assert.Equal(3, progress.EligibleInstallmentCount);
        Assert.Equal(1, progress.UntrackedEligibleInstallmentCount); // Volume 2 is untracked
        Assert.Equal(3, progress.KnownCanonicalTotalInstallmentCount);
        Assert.Equal(0, progress.UnknownCanonicalTotalInstallmentCount);
        Assert.Equal(450_000, progress.KnownCanonicalCharacterCount); // 100k + 200k + 150k

        // Volume 1 and Volume 2 are computable (300,000 weight)
        Assert.Equal(2, progress.ComputableProgressInstallmentCount);
        Assert.Equal(300_000, progress.ComputableProgressCharacterCount);

        // Volume 3 has explicit-zero copy total => unknown progress
        Assert.Equal(1, progress.UnknownProgressInstallmentCount);
        Assert.Equal(150_000, progress.UnknownProgressCharacterCount);

        // Non-headline items: Unknown release (Vol 4), Upcoming (Vol 5), Excluded (Side Story)
        Assert.Equal(1, progress.UnknownReleaseInstallmentCount);
        Assert.Equal(1, progress.UpcomingInstallmentCount);
        Assert.Equal(1, progress.ExcludedInstallmentCount);

        // Lifetime activity is independent sum of copies (1,000 + 10,000 + 500 = 11,500 ch; 15 + 60 + 10 = 85 min)
        Assert.Equal(11_500, progress.LifetimeCharactersRead);
        Assert.Equal(85d, progress.LifetimeMinutes);

        // Verify installments ordering and state in view model
        Assert.Equal(6, viewModel.Installments.Count);
        Assert.Equal("本", viewModel.TypeToJapanese);
        Assert.Equal("16.7%", viewModel.ProgressBar.Label);
        Assert.False(viewModel.ProgressBar.IsComplete);
        Assert.True(viewModel.ProgressBar.ShowPercentage);
        Assert.InRange(viewModel.ProgressBar.Percentage, 16.66, 16.67);

        var vol1 = viewModel.Installments[0];
        Assert.Equal("Volume 1", vol1.Title);
        Assert.True(vol1.IsTracked);
        Assert.Equal("Tracked", vol1.StatusBadgeLabel);
        Assert.Equal(2, vol1.Copies.Count);
        Assert.Equal(0.5, vol1.ProgressFraction);
        Assert.NotNull(vol1.ProgressBar);
        Assert.Equal(50d, vol1.ProgressBar.Percentage);

        var vol2 = viewModel.Installments[1];
        Assert.Equal("Volume 2", vol2.Title);
        Assert.False(vol2.IsTracked);
        Assert.Equal("Catalogue only", vol2.StatusBadgeLabel);
        Assert.Empty(vol2.Copies);
        Assert.Equal(0d, vol2.ProgressFraction);
        Assert.NotNull(vol2.ProgressBar);
        Assert.Equal(0d, vol2.ProgressBar.Percentage);

        // Volume 2 (catalogue-only) has no MediaWork in the database
        Assert.Empty(await database.Context.MediaWorks.AsNoTracking().Where(w => w.MediaInstallmentId == Volume2Id).ToListAsync());

        var vol3 = viewModel.Installments[2];
        Assert.Equal("Volume 3", vol3.Title);
        Assert.True(vol3.IsTracked);
        Assert.Null(vol3.ProgressFraction); // Unknown progress
        Assert.Null(vol3.ProgressBar);

        var sideStory = viewModel.Installments[5];
        Assert.Equal("Side Story 1", sideStory.Title);
        Assert.False(sideStory.IsIncluded);
    }

    [Fact]
    public async Task IndexPage_ListsSeriesWithMetricsAndHandlesFilterAndTruncation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedNorthWindNovelsFixtureAsync(database.Context);

        var model = new IndexModel(database.Context);
        await model.OnGetAsync(CancellationToken.None);

        var item = Assert.Single(model.Series);
        Assert.Equal("North Wind Novels", item.Title);
        Assert.Equal(MediaType.Book, item.MediaType);
        Assert.False(model.IsTruncated);
        Assert.NotNull(item.ProgressPercentage);
        Assert.InRange(item.ProgressPercentage.Value, 16.66, 16.67);

        // Filter for Anime returns empty
        model.Type = MediaType.Anime;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Empty(model.Series);

        // Filter for Book returns the series
        model.Type = MediaType.Book;
        await model.OnGetAsync(CancellationToken.None);
        Assert.Single(model.Series);
    }

    [Fact]
    public async Task CreatePage_PersistsSeriesAndRedirectsToDetails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateCreateModel(database.Context);
        model.Input = new CreateModel.SeriesInput
        {
            Title = "  Re:Zero Novels  ",
            MediaType = MediaType.Book
        };

        var result = await model.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Series/Details", redirect.PageName);
        var createdId = Assert.IsType<Guid>(redirect.RouteValues!["id"]);

        database.Context.ChangeTracker.Clear();
        var savedSeries = await database.Context.MediaSeries.AsNoTracking().SingleAsync(s => s.Id == createdId);
        Assert.Equal("Re:Zero Novels", savedSeries.Title);
        Assert.Equal(MediaType.Book, savedSeries.MediaType);
        Assert.Equal("Series 'Re:Zero Novels' created successfully.", model.TempData["LibraryNotice"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreatePage_RejectsEmptyTitle(string? emptyTitle)
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateCreateModel(database.Context);
        model.Input = new CreateModel.SeriesInput { Title = emptyTitle! };

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("Input.Title"));
        Assert.Empty(await database.Context.MediaSeries.ToListAsync());
    }

    [Fact]
    public async Task EditPage_UpdatesTitlePreservesMediaTypeAndRedirects()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Old Name", MediaType.Book);
        database.Context.MediaSeries.Add(series);
        await database.Context.SaveChangesAsync();

        var model = CreateEditModel(database.Context);
        var getResult = await model.OnGetAsync(series.Id, CancellationToken.None);
        Assert.IsType<PageResult>(getResult);
        Assert.Equal("Old Name", model.Input.Title);
        Assert.Equal(MediaType.Book, model.MediaType);

        model.Input.Title = "  New Name  ";
        var postResult = await model.OnPostAsync(series.Id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(postResult);
        Assert.Equal("/Series/Details", redirect.PageName);

        database.Context.ChangeTracker.Clear();
        var updated = await database.Context.MediaSeries.AsNoTracking().SingleAsync(s => s.Id == series.Id);
        Assert.Equal("New Name", updated.Title);
        Assert.Equal(MediaType.Book, updated.MediaType);
        Assert.Equal("Series updated successfully.", model.TempData["LibraryNotice"]);
    }

    [Fact]
    public async Task EditPage_ReturnsNotFoundForUnknownSeries()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateEditModel(database.Context);

        var result = await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DetailsPage_ReturnsNotFoundForUnknownSeries()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateDetailsModel(database.Context);

        var result = await model.OnGetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DetailsPage_AddInstallment_PersistsAndRedirects()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        database.Context.MediaSeries.Add(series);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostAddInstallmentAsync(series.Id, new DetailsModel.AddInstallmentInput
        {
            Title = "Volume 1",
            Kind = InstallmentKind.Volume,
            ReleaseState = ReleaseState.Released,
            CanonicalCharacterCount = 120_000,
            IsIncluded = true
        }, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(series.Id, redirect.RouteValues!["id"]);

        database.Context.ChangeTracker.Clear();
        var installment = await database.Context.MediaInstallments.AsNoTracking().SingleAsync(i => i.MediaSeriesId == series.Id);
        Assert.Equal("Volume 1", installment.TitleOverride);
        Assert.Equal(120_000, installment.CharacterCountOverride);
        Assert.Equal(120_000, installment.EffectiveCharacterCount);
        Assert.True(installment.IsIncluded);
    }

    [Fact]
    public async Task DetailsPage_EditInstallment_HandlesUpdateAndConcurrencyConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            CanonicalCharacterCount = 100_000,
            IsIncluded = true
        };
        database.Context.AddRange(series, installment);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);

        // 1. Successful edit: include -> exclude
        var successResult = await model.OnPostEditInstallmentAsync(series.Id, new DetailsModel.EditInstallmentInput
        {
            InstallmentId = installment.Id,
            ExpectedVersion = installment.Version,
            Title = "Volume 1 Updated",
            Kind = InstallmentKind.Volume,
            ReleaseStateOverride = ReleaseState.Released,
            IsIncluded = false,
            CanonicalCharacterCount = 105_000
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(successResult);
        database.Context.ChangeTracker.Clear();
        var updated = await database.Context.MediaInstallments.AsNoTracking().SingleAsync(i => i.Id == installment.Id);
        Assert.Equal("Volume 1 Updated", updated.TitleOverride);
        Assert.False(updated.IsIncluded);
        Assert.Equal(105_000, updated.CharacterCountOverride);
        Assert.Equal(105_000, updated.EffectiveCharacterCount);

        // 2. Concurrency conflict with stale version
        var conflictModel = CreateDetailsModel(database.Context);
        var conflictResult = await conflictModel.OnPostEditInstallmentAsync(series.Id, new DetailsModel.EditInstallmentInput
        {
            InstallmentId = installment.Id,
            ExpectedVersion = Guid.NewGuid(), // Stale version!
            Title = "Volume 1 Stale",
            Kind = InstallmentKind.Volume,
            IsIncluded = true
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(conflictResult);
        Assert.NotNull(conflictModel.TempData["LibraryError"]);
        Assert.Contains("refresh", (string)conflictModel.TempData["LibraryError"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailsPage_ReorderInstallments_UpdatesOrderKeys()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var first = new MediaInstallment("First", MediaType.Book, 100) { MediaSeries = series };
        var second = new MediaInstallment("Second", MediaType.Book, 200) { MediaSeries = series };
        database.Context.AddRange(series, first, second);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostReorderInstallmentsAsync(series.Id, new DetailsModel.ReorderInstallmentsInput
        {
            InstallmentId = second.Id,
            Direction = "up",
            ExpectedVersions = new Dictionary<Guid, Guid>
            {
                [first.Id] = first.Version,
                [second.Id] = second.Version
            }
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        var reordered = await database.Context.MediaInstallments.AsNoTracking()
            .Where(i => i.MediaSeriesId == series.Id)
            .OrderBy(i => i.OrderKey)
            .ToListAsync();

        Assert.Equal(second.Id, reordered[0].Id);
        Assert.Equal(first.Id, reordered[1].Id);
    }

    [Fact]
    public async Task DetailsPage_CreateCopy_CreatesWorkInSeriesAndLibrary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book, 100) { MediaSeries = series };
        database.Context.AddRange(series, installment);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostCreateCopyAsync(series.Id, new DetailsModel.CreateCopyInput
        {
            InstallmentId = installment.Id,
            Title = "Volume 1 E-Book"
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        var copy = await database.Context.MediaWorks.AsNoTracking().SingleAsync();
        Assert.Equal("Volume 1 E-Book", copy.Title);
        Assert.Equal(installment.Id, copy.MediaInstallmentId);
        Assert.Equal(series.Id, copy.MediaSeriesId);

        // Appears in Series details query
        var details = await new SeriesCatalogueQueryService(database.Context).GetDetailsAsync(series.Id);
        Assert.NotNull(details);
        var singleInstallment = Assert.Single(details.Installments);
        var singleCopy = Assert.Single(singleInstallment.Copies);
        Assert.Equal(copy.Id, singleCopy.Id);
    }

    [Fact]
    public async Task DetailsPage_RejectsCrossSeriesInstallmentAndInvalidEnumPosts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstSeries = new MediaSeries("First", MediaType.Book);
        var secondSeries = new MediaSeries("Second", MediaType.Book);
        var secondInstallment = new MediaInstallment("Second volume", MediaType.Book) { MediaSeries = secondSeries };
        database.Context.AddRange(firstSeries, secondSeries, secondInstallment);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);
        await model.OnPostCreateCopyAsync(firstSeries.Id, new DetailsModel.CreateCopyInput
        {
            InstallmentId = secondInstallment.Id,
            Title = "Tampered copy"
        }, CancellationToken.None);
        Assert.Contains("does not belong", Assert.IsType<string>(model.TempData["LibraryError"]));
        Assert.Empty(await database.Context.MediaWorks.AsNoTracking().ToListAsync());

        var invalidModel = CreateDetailsModel(database.Context);
        await invalidModel.OnPostAddInstallmentAsync(firstSeries.Id, new DetailsModel.AddInstallmentInput
        {
            Title = "Invalid",
            Kind = (InstallmentKind)999,
            ReleaseState = ReleaseState.Released
        }, CancellationToken.None);
        Assert.NotNull(invalidModel.TempData["LibraryError"]);
        Assert.Empty(await database.Context.MediaInstallments.AsNoTracking()
            .Where(item => item.MediaSeriesId == firstSeries.Id).ToListAsync());
    }

    [Fact]
    public async Task DetailsPage_OffersOnlyStandaloneCopiesForDirectAssociation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume", MediaType.Book) { MediaSeries = series };
        var assigned = new MediaWork("Assigned") { MediaInstallment = installment, MediaSeries = series };
        var standaloneInstallment = new MediaInstallment("Standalone", MediaType.Book);
        var standalone = new MediaWork("Standalone") { MediaInstallment = standaloneInstallment };
        database.Context.AddRange(series, installment, assigned, standaloneInstallment, standalone);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var model = CreateDetailsModel(database.Context);
        Assert.IsType<PageResult>(await model.OnGetAsync(series.Id, CancellationToken.None));
        var option = Assert.Single(model.SeriesDetails.AvailableCopies);
        Assert.Equal(standalone.Id, option.Id);
        Assert.Equal(standaloneInstallment.Id, option.SourceInstallmentId);
    }

    [Fact]
    public async Task DetailsPage_AssociateCopy_MovesCopyAndRetainsHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var installment = new MediaInstallment("Volume 1", MediaType.Book, 100) { MediaSeries = series };
        var standaloneInstallment = new MediaInstallment("Standalone Work", MediaType.Book);
        var standaloneWork = new MediaWork("Standalone Work", mediaType: MediaType.Book)
            { MediaInstallment = standaloneInstallment };
        standaloneWork.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 22), CharactersRead = 2_000, TimeSpentMinutes = 30 });
        database.Context.AddRange(series, installment, standaloneInstallment, standaloneWork);
        await database.Context.SaveChangesAsync();

        var logId = standaloneWork.Logs.First().Id;

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostAssociateCopyAsync(series.Id, new DetailsModel.AssociateCopyInput
        {
            InstallmentId = installment.Id,
            TargetExpectedVersion = installment.Version,
            CopyReview = string.Join('|', standaloneWork.Id, standaloneWork.Version,
                standaloneInstallment.Id, standaloneInstallment.Version)
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        var associated = await database.Context.MediaWorks.Include(w => w.Logs).AsNoTracking().SingleAsync(w => w.Id == standaloneWork.Id);
        Assert.Equal(installment.Id, associated.MediaInstallmentId);
        Assert.Equal(series.Id, associated.MediaSeriesId);
        var log = Assert.Single(associated.Logs);
        Assert.Equal(logId, log.Id);
        Assert.Equal(2_000, log.CharactersRead);
    }

    [Fact]
    public async Task DetailsPage_Consolidate_MovesCopiesPreservesSourceInstallmentAndLogs()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var sourceInst = new MediaInstallment("Volume 1 Duplicate", MediaType.Book, 100) { MediaSeries = series };
        var targetInst = new MediaInstallment("Volume 1 Canonical", MediaType.Book, 200) { MediaSeries = series };
        var copy = new MediaWork("Tracked Edition", mediaType: MediaType.Book) { MediaInstallment = sourceInst, MediaSeries = series };
        var log = new ImmersionLog { MediaWorkId = copy.Id, Date = new DateOnly(2026, 9, 22), CharactersRead = 4_500, TimeSpentMinutes = 45 };
        database.Context.AddRange(series, sourceInst, targetInst, copy, log);
        await database.Context.SaveChangesAsync();

        var logId = log.Id;
        var copyId = copy.Id;

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostConsolidateAsync(series.Id, new DetailsModel.ConsolidateInput
        {
            SourceInstallmentId = sourceInst.Id,
            SourceExpectedVersion = sourceInst.Version,
            TargetCombined = $"{targetInst.Id}|{targetInst.Version}",
            CopyIds = [copyId]
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        // Copy now belongs to target installment
        var movedCopy = await database.Context.MediaWorks.Include(w => w.Logs).AsNoTracking().SingleAsync(w => w.Id == copyId);
        Assert.Equal(targetInst.Id, movedCopy.MediaInstallmentId);
        var movedLog = Assert.Single(movedCopy.Logs);
        Assert.Equal(logId, movedLog.Id);
        Assert.Equal(4_500, movedLog.CharactersRead);

        // Source installment is preserved (not deleted)
        var sourceCheck = await database.Context.MediaInstallments.AsNoTracking().SingleOrDefaultAsync(i => i.Id == sourceInst.Id);
        Assert.NotNull(sourceCheck);
    }

    [Fact]
    public async Task DetailsPage_Consolidate_HandlesStaleVersionConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book);
        var sourceInst = new MediaInstallment("Volume 1 Duplicate", MediaType.Book, 100) { MediaSeries = series };
        var targetInst = new MediaInstallment("Volume 1 Canonical", MediaType.Book, 200) { MediaSeries = series };
        var copy = new MediaWork("Tracked Edition", mediaType: MediaType.Book) { MediaInstallment = sourceInst, MediaSeries = series };
        database.Context.AddRange(series, sourceInst, targetInst, copy);
        await database.Context.SaveChangesAsync();

        var model = CreateDetailsModel(database.Context);
        var result = await model.OnPostConsolidateAsync(series.Id, new DetailsModel.ConsolidateInput
        {
            SourceInstallmentId = sourceInst.Id,
            SourceExpectedVersion = Guid.NewGuid(), // Stale version!
            TargetCombined = $"{targetInst.Id}|{targetInst.Version}",
            CopyIds = [copy.Id]
        }, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(model.TempData["LibraryError"]);
        Assert.Contains("refresh", (string)model.TempData["LibraryError"]!, StringComparison.OrdinalIgnoreCase);

        // Copy was not moved
        database.Context.ChangeTracker.Clear();
        var unmoved = await database.Context.MediaWorks.AsNoTracking().SingleAsync(w => w.Id == copy.Id);
        Assert.Equal(sourceInst.Id, unmoved.MediaInstallmentId);
    }

    [Fact]
    public void DetailsMarkup_UncheckedInclusionPostsExplicitFalseForAddAndEdit()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kiseki.Web", "Pages", "Series", "Details.cshtml"),
            Path.Combine(AppContext.BaseDirectory, "Kiseki.Web", "Pages", "Series", "Details.cshtml")
        };
        var razorPath = candidatePaths.FirstOrDefault(File.Exists);
        Assert.NotNull(razorPath);

        var markup = File.ReadAllText(razorPath);
        const string explicitFalse = "<input type=\"hidden\" name=\"IsIncluded\" value=\"false\" />";
        Assert.Equal(2, markup.Split(explicitFalse, StringSplitOptions.None).Length - 1);
        Assert.Contains("id=\"addIsIncluded\" name=\"IsIncluded\" value=\"true\" checked", markup);
        Assert.Contains("id=\"editInc-@inst.Id\"", markup);
        Assert.Contains("class=\"btn btn-primary\" asp-page=\"/Series/RefreshJiten\"", markup);
        Assert.Contains("\"bg-info text-dark\"", markup);
    }

    [Fact]
    public void DetailsStyles_MutedSummaryTextOverridesBootstrapOnDarkSurfaces()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kiseki.Web", "wwwroot", "css", "site.css"),
            Path.Combine(AppContext.BaseDirectory, "Kiseki.Web", "wwwroot", "css", "site.css")
        };
        var stylesheetPath = candidatePaths.FirstOrDefault(File.Exists);
        Assert.NotNull(stylesheetPath);

        var stylesheet = File.ReadAllText(stylesheetPath);
        const string darkSurfaceMutedOverride = """
.summary-card .text-muted,
.technical-coverage-details .text-muted,
.series-non-headline-bar.text-muted {
  color: var(--color-ink-2) !important;
}
""";
        Assert.Contains(darkSurfaceMutedOverride, stylesheet);
    }

    [Fact]
    public async Task FourVolumeReferenceCase_DetailsAndIndex_ShowsExpectedCompletionTrackingAndProgress()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var vol1 = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        var vol2 = new MediaInstallment("Volume 2", MediaType.Book, 200)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        var vol3 = new MediaInstallment("Volume 3", MediaType.Book, 300)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        var vol4 = new MediaInstallment("Volume 4", MediaType.Book, 400)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };

        // Volume 1: completed copy (100k read)
        var copy1 = new MediaWork("Volume 1 Copy", mediaType: MediaType.Book)
        {
            MediaInstallment = vol1,
            MediaSeries = series,
            IsCompleted = true,
            ManualCharacterCountOverride = 100_000
        };
        copy1.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 20), CharactersRead = 100_000, TimeSpentMinutes = 300 });

        // Volume 2: halfway read copy (50k / 100k)
        var copy2 = new MediaWork("Volume 2 Copy", mediaType: MediaType.Book)
        {
            MediaInstallment = vol2,
            MediaSeries = series,
            IsCompleted = false,
            ManualCharacterCountOverride = 100_000
        };
        copy2.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 21), CharactersRead = 50_000, TimeSpentMinutes = 150 });

        // Volumes 3 and 4: no copies (catalogue only)
        database.Context.MediaSeries.Add(series);
        database.Context.MediaInstallments.AddRange(vol1, vol2, vol3, vol4);
        database.Context.MediaWorks.AddRange(copy1, copy2);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        // 1. Verify Details PageModel
        var detailsModel = CreateDetailsModel(database.Context);
        await detailsModel.OnGetAsync(series.Id, CancellationToken.None);
        var details = detailsModel.SeriesDetails;

        Assert.Equal(1, details.CompletedVolumeCount);
        Assert.Equal(4, details.ReleasedVolumeCount);
        Assert.Equal("1 / 4 released volumes completed", details.CompletedVolumesSummary);
        Assert.Equal(2, details.TrackedVolumeCount);
        Assert.Equal(2, details.UntrackedVolumeCount);
        Assert.Equal("2 tracked, 2 not tracked", details.TrackingSummary);
        Assert.NotNull(details.ProgressPercentage);
        Assert.Equal(37.5, details.ProgressPercentage.Value);
        Assert.False(details.HasPartialCoverage);
        Assert.Equal("In progress", details.StatusLabel);
        Assert.Equal("bg-primary", details.StatusBadgeCss);
        Assert.False(details.IsComplete);
        Assert.False(details.ProgressBar.IsComplete);

        // 2. Verify Index PageModel
        var indexModel = new IndexModel(database.Context);
        await indexModel.OnGetAsync(CancellationToken.None);
        var indexItem = Assert.Single(indexModel.Series);

        Assert.Equal(1, indexItem.CompletedVolumeCount);
        Assert.Equal(4, indexItem.ReleasedVolumeCount);
        Assert.Equal("1 / 4 completed", indexItem.CompletedVolumesSummary);
        Assert.Equal("2 tracked, 2 not tracked", indexItem.TrackingSummary);
        Assert.NotNull(indexItem.ProgressPercentage);
        Assert.Equal(37.5, indexItem.ProgressPercentage.Value);
        Assert.False(indexItem.HasPartialCoverage);
        Assert.Equal("In progress", indexItem.StatusLabel);
        Assert.Equal("media-status-active", indexItem.StatusCss);
        Assert.False(indexItem.IsComplete);

        // 3. Adding a second copy of Volume 1 must not alter denominator
        var copy1Second = new MediaWork("Volume 1 Paperback", mediaType: MediaType.Book)
        {
            MediaInstallmentId = vol1.Id,
            MediaSeriesId = series.Id,
            IsCompleted = false,
            ManualCharacterCountOverride = 100_000
        };
        database.Context.MediaWorks.Add(copy1Second);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var refreshedDetails = CreateDetailsModel(database.Context);
        await refreshedDetails.OnGetAsync(series.Id, CancellationToken.None);
        Assert.Equal(1, refreshedDetails.SeriesDetails.CompletedVolumeCount);
        Assert.Equal(4, refreshedDetails.SeriesDetails.ReleasedVolumeCount);
        Assert.Equal(2, refreshedDetails.SeriesDetails.TrackedVolumeCount);
        Assert.Equal(2, refreshedDetails.SeriesDetails.UntrackedVolumeCount);
        Assert.Equal(37.5, refreshedDetails.SeriesDetails.ProgressPercentage!.Value);
    }

    [Fact]
    public async Task FourVolumeReferenceCase_WithUnknownTotal_ShowsPartialCoverageAndMaintainsCompletion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Spice and Wolf", MediaType.Book);
        var vol1 = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        var vol2 = new MediaInstallment("Volume 2", MediaType.Book, 200)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        var vol3 = new MediaInstallment("Volume 3", MediaType.Book, 300)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        // Volume 4 has unknown canonical total
        var vol4 = new MediaInstallment("Volume 4", MediaType.Book, 400)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = null
        };

        var copy1 = new MediaWork("Volume 1 Copy", mediaType: MediaType.Book)
        {
            MediaInstallment = vol1,
            MediaSeries = series,
            IsCompleted = true,
            ManualCharacterCountOverride = 100_000
        };
        copy1.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 20), CharactersRead = 100_000, TimeSpentMinutes = 300 });

        var copy2 = new MediaWork("Volume 2 Copy", mediaType: MediaType.Book)
        {
            MediaInstallment = vol2,
            MediaSeries = series,
            IsCompleted = false,
            ManualCharacterCountOverride = 100_000
        };
        copy2.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 21), CharactersRead = 50_000, TimeSpentMinutes = 150 });

        database.Context.MediaSeries.Add(series);
        database.Context.MediaInstallments.AddRange(vol1, vol2, vol3, vol4);
        database.Context.MediaWorks.AddRange(copy1, copy2);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        // 1. Details PageModel
        var detailsModel = CreateDetailsModel(database.Context);
        await detailsModel.OnGetAsync(series.Id, CancellationToken.None);
        var details = detailsModel.SeriesDetails;

        Assert.Equal(1, details.CompletedVolumeCount);
        Assert.Equal(4, details.ReleasedVolumeCount);
        Assert.Equal("1 / 4 released volumes completed", details.CompletedVolumesSummary);
        Assert.Equal(2, details.TrackedVolumeCount);
        Assert.Equal(2, details.UntrackedVolumeCount);
        Assert.Equal(3, details.ComputableVolumeCount);
        Assert.True(details.HasPartialCoverage);
        Assert.Equal("covers 3 of 4 volumes", details.PartialCoverageLabel);
        // Computable progress: (100k * 1 + 100k * 0.5 + 100k * 0) / 300k = 50.0%
        Assert.NotNull(details.ProgressPercentage);
        Assert.Equal(50.0, details.ProgressPercentage.Value);
        Assert.False(details.IsComplete);
        Assert.Equal("In progress", details.StatusLabel);

        // 2. Index PageModel
        var indexModel = new IndexModel(database.Context);
        await indexModel.OnGetAsync(CancellationToken.None);
        var indexItem = Assert.Single(indexModel.Series);

        Assert.Equal(1, indexItem.CompletedVolumeCount);
        Assert.Equal(4, indexItem.ReleasedVolumeCount);
        Assert.Equal("1 / 4 completed", indexItem.CompletedVolumesSummary);
        Assert.True(indexItem.HasPartialCoverage);
        Assert.Equal("covers 3 of 4 volumes", indexItem.PartialCoverageLabel);
        Assert.Contains("covers 3 of 4 volumes", indexItem.ProgressBar.Label);
        Assert.False(indexItem.IsComplete);
        Assert.Equal("In progress", indexItem.StatusLabel);
    }

    [Fact]
    public async Task SubsetWith100PercentCharacterProgress_DoesNotMarkWholeSeriesComplete()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Two Volume Series", MediaType.Book);
        var vol1 = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };
        // Volume 2 is released, untracked, and has no canonical count
        var vol2 = new MediaInstallment("Volume 2", MediaType.Book, 200)
        {
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = null
        };

        // Volume 1 is 100% completed
        var copy1 = new MediaWork("Volume 1 Copy", mediaType: MediaType.Book)
        {
            MediaInstallment = vol1,
            MediaSeries = series,
            IsCompleted = true,
            ManualCharacterCountOverride = 100_000
        };
        copy1.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 20), CharactersRead = 100_000, TimeSpentMinutes = 300 });

        database.Context.MediaSeries.Add(series);
        database.Context.MediaInstallments.AddRange(vol1, vol2);
        database.Context.MediaWorks.Add(copy1);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        // 1. Details
        var detailsModel = CreateDetailsModel(database.Context);
        await detailsModel.OnGetAsync(series.Id, CancellationToken.None);
        var details = detailsModel.SeriesDetails;

        Assert.Equal(1, details.CompletedVolumeCount);
        Assert.Equal(2, details.ReleasedVolumeCount);
        Assert.Equal(100.0, details.ProgressPercentage!.Value);
        Assert.True(details.HasPartialCoverage);
        Assert.Equal("covers 1 of 2 volumes", details.PartialCoverageLabel);
        // CRITICAL INVARIANT: Series is NOT complete even though ProgressPercentage is 100%!
        Assert.False(details.IsComplete);
        Assert.Equal("In progress", details.StatusLabel);
        Assert.Equal("bg-primary", details.StatusBadgeCss);
        Assert.False(details.ProgressBar.IsComplete);

        // 2. Index
        var indexModel = new IndexModel(database.Context);
        await indexModel.OnGetAsync(CancellationToken.None);
        var indexItem = Assert.Single(indexModel.Series);

        Assert.Equal(1, indexItem.CompletedVolumeCount);
        Assert.Equal(2, indexItem.ReleasedVolumeCount);
        Assert.Equal(100.0, indexItem.ProgressPercentage!.Value);
        Assert.False(indexItem.IsComplete);
        Assert.Equal("In progress", indexItem.StatusLabel);
        Assert.Equal("media-status-active", indexItem.StatusCss);
        Assert.False(indexItem.ProgressBar.IsComplete);
    }

    [Fact]
    public async Task EmptyCatalogueAndNoReleasedVolumes_HandledHonestlyWithoutFalseZeroCompletion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var emptySeries = new MediaSeries("Empty Series", MediaType.Book);
        var upcomingSeries = new MediaSeries("Upcoming Only", MediaType.Book);

        var upcomingVol = new MediaInstallment("Upcoming Vol", MediaType.Book, 100)
        {
            MediaSeries = upcomingSeries,
            ReleaseState = ReleaseState.Upcoming,
            IsIncluded = true
        };
        var unknownRelVol = new MediaInstallment("Unknown Rel Vol", MediaType.Book, 200)
        {
            MediaSeries = upcomingSeries,
            ReleaseState = ReleaseState.Unknown,
            IsIncluded = true
        };

        database.Context.MediaSeries.AddRange(emptySeries, upcomingSeries);
        database.Context.MediaInstallments.AddRange(upcomingVol, unknownRelVol);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        // 1. Empty series Details
        var detailsModel1 = CreateDetailsModel(database.Context);
        await detailsModel1.OnGetAsync(emptySeries.Id, CancellationToken.None);
        var details1 = detailsModel1.SeriesDetails;

        Assert.Equal(0, details1.ReleasedVolumeCount);
        Assert.Equal(0, details1.CompletedVolumeCount);
        Assert.Equal("No volumes in catalogue", details1.CompletedVolumesSummary);
        Assert.Null(details1.ProgressPercentage);
        Assert.False(details1.IsComplete);
        Assert.Equal("Not started", details1.StatusLabel);
        Assert.Equal("No computable progress", details1.ProgressBar.Label);
        Assert.False(details1.ProgressBar.ShowPercentage);

        // 2. Upcoming/Unknown series Details
        var detailsModel2 = CreateDetailsModel(database.Context);
        await detailsModel2.OnGetAsync(upcomingSeries.Id, CancellationToken.None);
        var details2 = detailsModel2.SeriesDetails;

        Assert.Equal(0, details2.ReleasedVolumeCount);
        Assert.Equal(0, details2.CompletedVolumeCount);
        Assert.Equal("0 released volumes", details2.CompletedVolumesSummary);
        Assert.Null(details2.ProgressPercentage);
        Assert.False(details2.IsComplete);
        Assert.Equal("Not started", details2.StatusLabel);
        Assert.True(details2.HasNonHeadlineVolumes);
        Assert.Equal(1, details2.UnknownReleaseCount);
        Assert.Equal(1, details2.UpcomingCount);
    }

    private static async Task SeedNorthWindNovelsFixtureAsync(ImmersionDbContext context)
    {
        var series = new MediaSeries("North Wind Novels", MediaType.Book) { Id = SeriesId };

        var vol1 = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            Id = Volume1Id,
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 100_000
        };

        var vol2 = new MediaInstallment("Volume 2", MediaType.Book, 200)
        {
            Id = Volume2Id,
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 200_000
        };

        var vol3 = new MediaInstallment("Volume 3", MediaType.Book, 300)
        {
            Id = Volume3Id,
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = true,
            CanonicalCharacterCount = 150_000
        };

        var vol4 = new MediaInstallment("Volume 4", MediaType.Book, 400)
        {
            Id = Volume4Id,
            MediaSeries = series,
            ReleaseState = ReleaseState.Unknown,
            IsIncluded = true,
            CanonicalCharacterCount = 50_000
        };

        var vol5 = new MediaInstallment("Volume 5", MediaType.Book, 500)
        {
            Id = Volume5Id,
            MediaSeries = series,
            ReleaseState = ReleaseState.Upcoming,
            IsIncluded = true,
            CanonicalCharacterCount = 80_000
        };

        var sideStory = new MediaInstallment("Side Story 1", MediaType.Book, 600)
        {
            Id = SideStoryId,
            MediaSeries = series,
            ReleaseState = ReleaseState.Released,
            IsIncluded = false,
            CanonicalCharacterCount = 30_000
        };

        // Copies for Volume 1:
        // Copy 1A: 10% (1,000 / 10,000 ch, 15 min)
        var copy1A = new MediaWork("Volume 1 E-Book", mediaType: MediaType.Book)
        {
            Id = Copy1AId,
            MediaInstallment = vol1,
            MediaSeries = series,
            ManualCharacterCountOverride = 10_000
        };
        copy1A.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 20), CharactersRead = 1_000, TimeSpentMinutes = 15 });

        // Copy 1B: 50% (10,000 / 20,000 ch, 60 min)
        var copy1B = new MediaWork("Volume 1 Paperback", mediaType: MediaType.Book)
        {
            Id = Copy1BId,
            MediaInstallment = vol1,
            MediaSeries = series,
            ManualCharacterCountOverride = 20_000
        };
        copy1B.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 21), CharactersRead = 10_000, TimeSpentMinutes = 60 });

        // Copy for Volume 3: tracked copy with explicit-zero total (unknown progress), 500 ch, 10 min
        var copy3 = new MediaWork("Volume 3 Edition", mediaType: MediaType.Book)
        {
            Id = Copy3Id,
            MediaInstallment = vol3,
            MediaSeries = series,
            ManualCharacterCountOverride = 0
        };
        copy3.Logs.Add(new ImmersionLog { Date = new DateOnly(2026, 9, 22), CharactersRead = 500, TimeSpentMinutes = 10 });

        context.MediaSeries.Add(series);
        context.MediaInstallments.AddRange(vol1, vol2, vol3, vol4, vol5, sideStory);
        context.MediaWorks.AddRange(copy1A, copy1B, copy3);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static DetailsModel CreateDetailsModel(ImmersionDbContext context)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        var model = new DetailsModel(context)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
        return model;
    }

    private static CreateModel CreateCreateModel(ImmersionDbContext context)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        var model = new CreateModel(context)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
        return model;
    }

    private static EditModel CreateEditModel(ImmersionDbContext context)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        var model = new EditModel(context)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
        return model;
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) =>
            _values = new(values);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ImmersionDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ImmersionDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new ImmersionDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        private TestDatabase(SqliteConnection connection, ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
