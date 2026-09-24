using System.Net;
using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Franchises;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kiseki.Tests;

public sealed class FranchisePageTests
{
    [Fact]
    public async Task Index_ListsFranchises_AndEmptyStateWhenNone()
    {
        await using var database = await TestDatabase.CreateAsync();

        // 1. Empty state
        var indexModel = CreateIndexModel(database.Context);
        await indexModel.OnGetAsync(CancellationToken.None);
        Assert.Empty(indexModel.Franchises);

        // 2. Seed franchises with member series of different types
        var f1 = new Franchise("Re:Zero") { JitenAnchorDeckId = 10 };
        var s1 = new MediaSeries("Re:Zero Light Novels", MediaType.Book) { Franchise = f1 };
        var s2 = new MediaSeries("Re:Zero Ex Novels", MediaType.Book) { Franchise = f1 };
        var s3 = new MediaSeries("Re:Zero Anime Season 1", MediaType.Anime) { Franchise = f1 };
        var s4 = new MediaSeries("Re:Zero Visual Novel", MediaType.Game) { Franchise = f1 };

        var f2 = new Franchise("Standalone Franchise");

        database.Context.AddRange(f1, f2, s1, s2, s3, s4);
        await database.Context.SaveChangesAsync();

        // 3. Populated state
        indexModel = CreateIndexModel(database.Context);
        await indexModel.OnGetAsync(CancellationToken.None);

        Assert.Equal(2, indexModel.Franchises.Count);

        var rez = Assert.Single(indexModel.Franchises, f => f.Id == f1.Id);
        Assert.Equal("Re:Zero", rez.Title);
        Assert.Equal(10, rez.JitenAnchorDeckId);
        Assert.Equal(4, rez.SeriesCount);
        Assert.Equal(2, rez.BookSeriesCount);
        Assert.Equal(1, rez.AnimeSeriesCount);
        Assert.Equal(1, rez.OtherSeriesCount);

        var emptyF = Assert.Single(indexModel.Franchises, f => f.Id == f2.Id);
        Assert.Equal("Standalone Franchise", emptyF.Title);
        Assert.Null(emptyF.JitenAnchorDeckId);
        Assert.Equal(0, emptyF.SeriesCount);
    }

    [Fact]
    public async Task Create_ValidInput_CreatesFranchiseAndRedirectsToDetails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FranchiseCatalogueService(database.Context);
        var createModel = CreateCreateModel(service);

        createModel.Input = new CreateModel.CreateFranchiseInput
        {
            Title = "Steins;Gate",
            JitenAnchorDeckId = 42
        };

        var result = await createModel.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Franchises/Details", redirect.PageName);
        var createdId = Assert.IsType<Guid>(redirect.RouteValues?["id"]);

        var franchise = await database.Context.Franchises.SingleAsync(f => f.Id == createdId);
        Assert.Equal("Steins;Gate", franchise.Title);
        Assert.Equal(42, franchise.JitenAnchorDeckId);
    }

    [Fact]
    public async Task Create_InvalidInput_ReturnsPageWithValidationError()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FranchiseCatalogueService(database.Context);
        var createModel = CreateCreateModel(service);

        createModel.Input = new CreateModel.CreateFranchiseInput
        {
            Title = "",
            JitenAnchorDeckId = -5
        };
        createModel.ModelState.AddModelError(nameof(createModel.Input.Title), "Title is required.");
        createModel.ModelState.AddModelError(nameof(createModel.Input.JitenAnchorDeckId), "Invalid anchor deck.");

        var result = await createModel.OnPostAsync(CancellationToken.None);
        Assert.IsType<PageResult>(result);
        Assert.False(createModel.ModelState.IsValid);
        Assert.Empty(await database.Context.Franchises.ToListAsync());
    }

    [Fact]
    public async Task Edit_UpdatesFranchise_AndDeleteUnassignsMembersWithoutDataLoss()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Original Title") { JitenAnchorDeckId = 100 };
        var bookSeries = new MediaSeries("Book Line", MediaType.Book) { Franchise = franchise };
        var installment = new MediaInstallment("Vol 1", MediaType.Book, 100) { MediaSeries = bookSeries };
        var copy = new MediaWork("E-Book", mediaType: MediaType.Book)
        {
            MediaInstallment = installment,
            MediaInstallmentId = installment.Id,
            MediaSeries = bookSeries,
            MediaSeriesId = bookSeries.Id
        };
        var log = new ImmersionLog
        {
            Date = new DateOnly(2026, 9, 20),
            CharactersRead = 5000,
            TimeSpentMinutes = 45d,
            Source = "ttsu",
            MediaWorkId = copy.Id
        };
        copy.Logs.Add(log);
        var animeSeries = new MediaSeries("Anime Line", MediaType.Anime) { Franchise = franchise };

        database.Context.AddRange(franchise, bookSeries, installment, copy, log, animeSeries);
        await database.Context.SaveChangesAsync();

        var service = new FranchiseCatalogueService(database.Context);
        var editModel = CreateEditModel(database.Context, service);

        // 1. NotFound for missing franchise
        var notFoundResult = await editModel.OnGetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(notFoundResult);

        // 2. OnGet loads franchise data
        var getResult = await editModel.OnGetAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<PageResult>(getResult);
        Assert.Equal("Original Title", editModel.Input.Title);
        Assert.Equal(100, editModel.Input.JitenAnchorDeckId);

        // 3. Edit title and anchor deck
        editModel.Input = new EditModel.EditFranchiseInput
        {
            Title = "Updated Title",
            JitenAnchorDeckId = 101
        };
        var editResult = await editModel.OnPostEditAsync(franchise.Id, CancellationToken.None);
        var redirect = Assert.IsType<RedirectToPageResult>(editResult);
        Assert.Equal("/Franchises/Details", redirect.PageName);

        database.Context.ChangeTracker.Clear();
        var updated = await database.Context.Franchises.SingleAsync(f => f.Id == franchise.Id);
        Assert.Equal("Updated Title", updated.Title);
        Assert.Equal(101, updated.JitenAnchorDeckId);

        // 4. Delete franchise container: must unassign members and preserve all series, installments, copies, logs
        var deleteResult = await editModel.OnPostDeleteAsync(franchise.Id, CancellationToken.None);
        var deleteRedirect = Assert.IsType<RedirectToPageResult>(deleteResult);
        Assert.Equal("/Franchises/Index", deleteRedirect.PageName);

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.Franchises.ToListAsync());

        var savedBookSeries = await database.Context.MediaSeries.SingleAsync(s => s.Id == bookSeries.Id);
        Assert.Null(savedBookSeries.FranchiseId);

        var savedAnimeSeries = await database.Context.MediaSeries.SingleAsync(s => s.Id == animeSeries.Id);
        Assert.Null(savedAnimeSeries.FranchiseId);

        var savedInstallment = await database.Context.MediaInstallments.SingleAsync(i => i.Id == installment.Id);
        Assert.Equal(bookSeries.Id, savedInstallment.MediaSeriesId);

        var savedCopy = await database.Context.MediaWorks.SingleAsync(w => w.Id == copy.Id);
        Assert.Equal(installment.Id, savedCopy.MediaInstallmentId);

        var savedLog = await database.Context.ImmersionLogs.SingleAsync(l => l.Id == log.Id);
        Assert.Equal(copy.Id, savedLog.MediaWorkId);
        Assert.Equal(5000, savedLog.CharactersRead);
    }

    [Fact]
    public async Task Details_DisplaysGroupedMediaWithoutCrossMediaPercentage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Monogatari");
        var book = new MediaSeries("Monogatari Novels", MediaType.Book) { Franchise = franchise };
        var anime = new MediaSeries("Monogatari Anime", MediaType.Anime) { Franchise = franchise };
        var game = new MediaSeries("Monogatari Portable", MediaType.Game) { Franchise = franchise };

        var volume = new MediaInstallment("Bakemonogatari Vol 1", MediaType.Book, 100)
        {
            MediaSeries = book,
            CanonicalCharacterCount = 120_000,
            ReleaseState = ReleaseState.Released
        };
        var copy = new MediaWork("Novel copy", mediaType: MediaType.Book)
        {
            MediaInstallment = volume,
            MediaInstallmentId = volume.Id,
            MediaSeries = book,
            MediaSeriesId = book.Id,
            IsCompleted = true
        };
        var log = new ImmersionLog
        {
            Date = new DateOnly(2026, 9, 21),
            CharactersRead = 120_000,
            TimeSpentMinutes = 150d,
            Source = "ttsu",
            MediaWorkId = copy.Id
        };
        copy.Logs.Add(log);

        var animeCopy = new MediaWork("Anime season", mediaType: MediaType.Anime)
        {
            MediaSeries = anime,
            MediaSeriesId = anime.Id
        };
        var animeLog = new ImmersionLog
        {
            Date = new DateOnly(2026, 9, 21),
            CharactersRead = 0,
            TimeSpentMinutes = 300d,
            Source = "manual",
            MediaWorkId = animeCopy.Id
        };
        animeCopy.Logs.Add(animeLog);

        database.Context.AddRange(franchise, book, anime, game, volume, copy, log, animeCopy, animeLog);
        await database.Context.SaveChangesAsync();

        var detailsModel = CreateDetailsModel(database.Context);
        var result = await detailsModel.OnGetAsync(franchise.Id, null, null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        var details = detailsModel.FranchiseDetails;
        Assert.NotNull(details);
        Assert.Equal(3, details.Series.Count);

        // 1. Books: Unit is Characters, headline progress exists, progress bar exists
        var bookItem = Assert.Single(details.BookSeries);
        Assert.Equal("Monogatari Novels", bookItem.Title);
        Assert.Equal(FranchiseProgressUnit.Characters, bookItem.ProgressUnit);
        Assert.Equal("100%", bookItem.HeadlineProgressText);
        Assert.NotNull(bookItem.ProgressBar);
        Assert.Equal(100d, bookItem.ProgressBar.Percentage);
        Assert.Equal(120_000, bookItem.LifetimeCharactersRead);
        Assert.Equal(150d, bookItem.LifetimeMinutes);

        // 2. Anime: Unit is NotAvailable, headline progress is "Not available", ProgressBar is null
        var animeItem = Assert.Single(details.AnimeSeries);
        Assert.Equal("Monogatari Anime", animeItem.Title);
        Assert.Equal(FranchiseProgressUnit.NotAvailable, animeItem.ProgressUnit);
        Assert.Equal("Not available", animeItem.HeadlineProgressText);
        Assert.Null(animeItem.ProgressBar);
        Assert.Equal(0d, animeItem.LifetimeMinutes);

        // 3. Other (Game): Unit is NotAvailable
        var gameItem = Assert.Single(details.OtherSeries);
        Assert.Equal("Monogatari Portable", gameItem.Title);
        Assert.Equal(FranchiseProgressUnit.NotAvailable, gameItem.ProgressUnit);

        // 4. Critical Invariant: No combined progress property exists
        Assert.DoesNotContain(typeof(FranchiseDetailsViewModel).GetProperties(), p =>
            p.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase) && !p.Name.StartsWith("BookSeries", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MoveAndUnassignSeries_UpdatesFranchiseMembershipCorrectly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var f1 = new Franchise("Franchise One");
        var f2 = new Franchise("Franchise Two");
        var series = new MediaSeries("Target Series", MediaType.Book) { Franchise = f2 };

        database.Context.AddRange(f1, f2, series);
        await database.Context.SaveChangesAsync();

        var detailsModel = CreateDetailsModel(database.Context);

        // 1. Move series from f2 to f1
        detailsModel.MoveSeriesId = series.Id;
        var moveResult = await detailsModel.OnPostMoveSeriesAsync(f1.Id, CancellationToken.None);
        var moveRedirect = Assert.IsType<RedirectToPageResult>(moveResult);
        Assert.Equal("/Franchises/Details", moveRedirect.PageName);

        database.Context.ChangeTracker.Clear();
        var movedSeries = await database.Context.MediaSeries.SingleAsync(s => s.Id == series.Id);
        Assert.Equal(f1.Id, movedSeries.FranchiseId);

        // 2. Unassign series from f1
        detailsModel.UnassignSeriesId = series.Id;
        var unassignResult = await detailsModel.OnPostUnassignSeriesAsync(f1.Id, CancellationToken.None);
        var unassignRedirect = Assert.IsType<RedirectToPageResult>(unassignResult);
        Assert.Equal("/Franchises/Details", unassignRedirect.PageName);

        database.Context.ChangeTracker.Clear();
        var unassignedSeries = await database.Context.MediaSeries.SingleAsync(s => s.Id == series.Id);
        Assert.Null(unassignedSeries.FranchiseId);
    }

    [Fact]
    public async Task TopologyPreviewAndApply_CompleteGraph_CreatesAndLinksSeriesWithReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Raildex");
        var existingIndexSeries = new MediaSeries("Index Novels", MediaType.Book) { JitenDeckId = 10 };
        database.Context.AddRange(franchise, existingIndexSeries);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(Graph(false,
            Node(10, "A Certain Magical Index", 4),
            Node(20, "A Certain Scientific Railgun", 4),
            Node(30, "Index Anime", 2),
            new JitenFranchiseEdgeDTO { SourceDeckId = 10, TargetDeckId = 20, RelationshipType = 1 },
            new JitenFranchiseEdgeDTO { SourceDeckId = 10, TargetDeckId = 30, RelationshipType = 2 }));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();

        var detailsModel = CreateDetailsModel(database.Context, client, store);

        // 1. Preview graph anchored at deck 10
        detailsModel.PreviewAnchorDeckId = 10;
        var previewResult = await detailsModel.OnPostPreviewTopologyAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<PageResult>(previewResult);

        var review = detailsModel.TopologyReview;
        Assert.NotNull(review);
        Assert.True(review.IsCompleteGraph);
        Assert.False(review.IsTruncated);
        Assert.Equal(3, review.Proposals.Count);

        var p10 = Assert.Single(review.Proposals, p => p.DeckId == 10);
        Assert.Equal(existingIndexSeries.Id, p10.ExactSeriesId);
        Assert.Equal(JitenFranchiseNodeAction.LinkExistingSeries, p10.RecommendedAction);

        var p20 = Assert.Single(review.Proposals, p => p.DeckId == 20);
        Assert.Equal(JitenFranchiseNodeProposalKind.NewBookSeries, p20.Kind);
        Assert.Contains(JitenFranchiseNodeAction.CreateSeparateBookSeries, p20.AllowedActions);

        var p30 = Assert.Single(review.Proposals, p => p.DeckId == 30);
        Assert.Equal(JitenFranchiseNodeClassification.Unknown, p30.Classification);
        Assert.DoesNotContain(JitenFranchiseNodeAction.LinkExistingSeries, p30.AllowedActions);
        Assert.DoesNotContain(JitenFranchiseNodeAction.CreateSeparateBookSeries, p30.AllowedActions);

        // 2. Submit choices
        var opId = Guid.NewGuid();
        detailsModel.ApplyReviewId = review.ReviewId;
        detailsModel.ApplyExpectedFingerprint = review.Fingerprint;
        detailsModel.ApplyOperationId = opId;
        detailsModel.Choices =
        [
            new() { ProposalId = p10.ProposalId, Action = JitenFranchiseNodeAction.LinkExistingSeries, MediaSeriesId = existingIndexSeries.Id },
            new() { ProposalId = p20.ProposalId, Action = JitenFranchiseNodeAction.CreateSeparateBookSeries },
            new() { ProposalId = p30.ProposalId, Action = JitenFranchiseNodeAction.KeepUnresolved }
        ];

        var applyResult = await detailsModel.OnPostApplyTopologyAsync(franchise.Id, CancellationToken.None);
        var applyRedirect = Assert.IsType<RedirectToPageResult>(applyResult);
        Assert.Equal("/Franchises/Details", applyRedirect.PageName);
        Assert.Equal(opId, applyRedirect.RouteValues?["operationId"]);

        // 3. Verify in DB
        database.Context.ChangeTracker.Clear();
        var seriesInFranchise = await database.Context.MediaSeries
            .Where(s => s.FranchiseId == franchise.Id)
            .OrderBy(s => s.JitenDeckId)
            .ToListAsync();
        Assert.Equal(2, seriesInFranchise.Count);
        Assert.Equal([10, 20], seriesInFranchise.Select(s => s.JitenDeckId));

        var unresolvedNode = await database.Context.JitenFranchiseGraphNodeStates.SingleAsync(n => n.DeckId == 30);
        Assert.Equal(JitenFranchiseNodeResolution.Unresolved, unresolvedNode.Resolution);

        // 4. OnGetAsync loads receipt
        var getReceiptResult = await detailsModel.OnGetAsync(franchise.Id, opId, false, CancellationToken.None);
        Assert.IsType<PageResult>(getReceiptResult);
        var receipt = detailsModel.Receipt;
        Assert.NotNull(receipt);
        Assert.Equal(1, receipt.CreatedSeries);
        Assert.Equal(2, receipt.LinkedSeries);
        Assert.Equal(0, receipt.IgnoredNodes);
        Assert.Equal(1, receipt.UnresolvedNodes);
        Assert.False(receipt.IsReplayed);
    }

    [Fact]
    public async Task TopologyPreview_TruncatedGraph_ShowsProminentWarningAndOnlyIgnoreOrUnresolved()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Huge Franchise");
        var existing = new MediaSeries("Series 1", MediaType.Book) { JitenDeckId = 10 };
        database.Context.AddRange(franchise, existing);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(Graph(true,
            Node(10, "Series 1", 4),
            Node(20, "Series 2", 4)));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();
        var detailsModel = CreateDetailsModel(database.Context, client, store);

        detailsModel.PreviewAnchorDeckId = 10;
        var result = await detailsModel.OnPostPreviewTopologyAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<PageResult>(result);

        var review = detailsModel.TopologyReview;
        Assert.NotNull(review);
        Assert.True(review.IsTruncated);
        Assert.False(review.IsCompleteGraph);

        // Truncated graph restricts all allowed actions to Ignore or KeepUnresolved
        foreach (var prop in review.Proposals)
        {
            Assert.DoesNotContain(JitenFranchiseNodeAction.LinkExistingSeries, prop.AllowedActions);
            Assert.DoesNotContain(JitenFranchiseNodeAction.CreateSeparateBookSeries, prop.AllowedActions);
            Assert.Subset(new HashSet<JitenFranchiseNodeAction>
            {
                JitenFranchiseNodeAction.Ignore,
                JitenFranchiseNodeAction.KeepUnresolved
            }, prop.AllowedActions.ToHashSet());
        }

        // Apply Ignore on deck 20 and KeepUnresolved on deck 10
        var opId = Guid.NewGuid();
        detailsModel.ApplyReviewId = review.ReviewId;
        detailsModel.ApplyExpectedFingerprint = review.Fingerprint;
        detailsModel.ApplyOperationId = opId;
        detailsModel.Choices = review.Proposals.Select(p => new FranchiseTopologyChoiceInput
        {
            ProposalId = p.ProposalId,
            Action = p.DeckId == 20 ? JitenFranchiseNodeAction.Ignore : JitenFranchiseNodeAction.KeepUnresolved
        }).ToList();

        var applyResult = await detailsModel.OnPostApplyTopologyAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(applyResult);

        database.Context.ChangeTracker.Clear();
        // Existing series membership was unchanged (never linked)
        var s1 = await database.Context.MediaSeries.SingleAsync(s => s.Id == existing.Id);
        Assert.Null(s1.FranchiseId);

        var ignoredState = await database.Context.JitenFranchiseGraphNodeStates.SingleAsync(s => s.DeckId == 20);
        Assert.Equal(JitenFranchiseNodeResolution.Ignored, ignoredState.Resolution);
    }

    [Fact]
    public async Task TopologyApply_StaleReviewOrChangedProvider_RequiresReviewAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Fate");
        database.Context.Add(franchise);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(Graph(false, Node(10, "Fate/stay night", 4)));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();
        var detailsModel = CreateDetailsModel(database.Context, client, store);

        // Preview
        detailsModel.PreviewAnchorDeckId = 10;
        await detailsModel.OnPostPreviewTopologyAsync(franchise.Id, CancellationToken.None);
        var review = detailsModel.TopologyReview!;

        // Change provider graph before apply
        client.Graph = Graph(false, Node(10, "Fate/stay night [Realta Nua]", 4));

        detailsModel.ApplyReviewId = review.ReviewId;
        detailsModel.ApplyExpectedFingerprint = review.Fingerprint;
        detailsModel.ApplyOperationId = Guid.NewGuid();
        detailsModel.Choices =
        [
            new() { ProposalId = review.Proposals[0].ProposalId, Action = JitenFranchiseNodeAction.CreateSeparateBookSeries }
        ];

        var result = await detailsModel.OnPostApplyTopologyAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<PageResult>(result);
        Assert.NotNull(detailsModel.Error);
        Assert.Contains("Review", detailsModel.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview the Jiten graph again", detailsModel.Error, StringComparison.OrdinalIgnoreCase);

        // No series created
        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.MediaSeries.ToListAsync());
    }

    [Fact]
    public async Task TopologyApply_ReplaySameOperationId_ReturnsCommittedReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var franchise = new Franchise("Sword Art Online");
        database.Context.Add(franchise);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(Graph(false, Node(10, "SAO Main", 4)));
        var store = new InMemoryJitenFranchiseTopologyReviewStore();
        var detailsModel = CreateDetailsModel(database.Context, client, store);

        // Preview
        detailsModel.PreviewAnchorDeckId = 10;
        await detailsModel.OnPostPreviewTopologyAsync(franchise.Id, CancellationToken.None);
        var review = detailsModel.TopologyReview!;

        var operationId = Guid.NewGuid();
        detailsModel.ApplyReviewId = review.ReviewId;
        detailsModel.ApplyExpectedFingerprint = review.Fingerprint;
        detailsModel.ApplyOperationId = operationId;
        detailsModel.Choices =
        [
            new() { ProposalId = review.Proposals[0].ProposalId, Action = JitenFranchiseNodeAction.CreateSeparateBookSeries }
        ];

        var applyResult = await detailsModel.OnPostApplyTopologyAsync(franchise.Id, CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(applyResult);

        // Verify receipt loaded on replayed GET
        var freshStore = new InMemoryJitenFranchiseTopologyReviewStore();
        var reloadedModel = CreateDetailsModel(database.Context, client, freshStore);
        var getResult = await reloadedModel.OnGetAsync(franchise.Id, operationId, true, CancellationToken.None);

        Assert.IsType<PageResult>(getResult);
        Assert.NotNull(reloadedModel.Receipt);
        Assert.True(reloadedModel.Receipt.IsReplayed);
        Assert.Equal(1, reloadedModel.Receipt.CreatedSeries);

        // Verify no duplicate series or receipts created
        Assert.Single(await database.Context.MediaSeries.ToListAsync());
        Assert.Single(await database.Context.JitenFranchiseTopologyReceipts.ToListAsync());
    }

    // --- Helper factories ---

    private static IndexModel CreateIndexModel(ImmersionDbContext context)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        return new IndexModel(context)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
    }

    private static CreateModel CreateCreateModel(FranchiseCatalogueService service)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        return new CreateModel(service)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
    }

    private static EditModel CreateEditModel(ImmersionDbContext context, FranchiseCatalogueService service)
    {
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        return new EditModel(context, service)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
    }

    private static DetailsModel CreateDetailsModel(
        ImmersionDbContext context,
        IJitenApiClient? client = null,
        IJitenFranchiseTopologyReviewStore? store = null)
    {
        client ??= new StubClient(Graph(false));
        store ??= new InMemoryJitenFranchiseTopologyReviewStore();

        var franchiseService = new FranchiseCatalogueService(context);
        var topologyService = new JitenFranchiseTopologyService(context, client, store);
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();

        return new DetailsModel(
            context,
            franchiseService,
            topologyService,
            NullLogger<DetailsModel>.Instance)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
    }

    private static JitenFranchiseNodeDTO Node(int id, string title, int mediaType) => new()
    {
        DeckId = id,
        OriginalTitle = title,
        MediaType = mediaType,
        CharacterCount = 1_000,
        ChildrenDeckCount = 1
    };

    private static JitenFranchiseDTO Graph(bool truncated, params JitenFranchiseNodeDTO[] nodes) => new()
    {
        Truncated = truncated,
        Nodes = nodes.ToList()
    };

    private static JitenFranchiseDTO Graph(bool truncated, JitenFranchiseNodeDTO first,
        JitenFranchiseNodeDTO second, JitenFranchiseNodeDTO third, params JitenFranchiseEdgeDTO[] edges) => new()
    {
        Truncated = truncated,
        Nodes = [first, second, third],
        Edges = edges.ToList()
    };

    private sealed class StubClient(JitenFranchiseDTO graph) : IJitenApiClient
    {
        public JitenFranchiseDTO Graph { get; set; } = graph;

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<JitenFranchiseDTO?>(Graph);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenDeckDetailDTO?>(null);

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenDeckCatalogueFetchResult> GetDeckCatalogueAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JitenDeckCatalogueFetchResult(new JitenDeckDetailDTO(), true, 0, 0));
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
