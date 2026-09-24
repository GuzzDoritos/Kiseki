using System.Net;
using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Series;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kiseki.Tests;

public sealed class SeriesJitenRefreshPageTests
{
    [Fact]
    public async Task NorthWind_PreviewAndApply_HydratesCatalogueWithoutCreatingCopies_AndPreservesManualAndCopyMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("North Wind", MediaType.Book) { JitenDeckId = 10 };
        var existing = new MediaInstallment("Legacy Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series,
            CanonicalTitle = "Old provider title",
            TitleOverride = "My title",
            CanonicalCharacterCount = 80_000,
            CharacterCountOverride = 81_234,
            ReleaseStateOverride = ReleaseState.Released,
            ReleaseDateOverride = new DateOnly(2020, 1, 2)
        };
        existing.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "subdeck:10:101",
            MediaInstallment = existing,
            ProviderItemId = 101,
            ParentProviderItemId = 10
        });
        var copy = new MediaWork("My ebook")
        {
            MediaInstallment = existing,
            MediaInstallmentId = existing.Id,
            MediaSeries = series,
            MediaSeriesId = series.Id
        };
        copy.ApplyTtsuCover("https://example.test/protected.jpg");
        database.Context.AddRange(series, existing, copy);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(10,
            Deck(101, "Provider one", 90_000, "https://cdn.jiten.moe/one.jpg"),
            Deck(102, "Provider two", 95_000, "http://unsafe.example/two.jpg")));
        var store = new InMemoryJitenCatalogueReviewStore();
        var reconciliationService = new JitenCatalogueReconciliationService(database.Context, client, store);
        var page = CreatePageModel(database.Context, reconciliationService, store);

        // 1. Initial GET shows anchored prompt
        var getResult = await page.OnGetAsync(series.Id, null, null, CancellationToken.None);
        Assert.IsType<PageResult>(getResult);
        Assert.Equal(RefreshJitenUiState.DeckPrompt, page.ViewModel.State);
        Assert.Equal(10, page.ViewModel.JitenDeckId);

        // 2. POST Preview loads proposals
        var previewResult = await page.OnPostPreviewAsync(series.Id, 10, CancellationToken.None);
        Assert.IsType<PageResult>(previewResult);
        Assert.Equal(RefreshJitenUiState.Reviewing, page.ViewModel.State);
        Assert.Equal(2, page.ViewModel.Proposals!.Count);

        var exactProposal = page.ViewModel.Proposals.Single(p => p.Kind == JitenCatalogueProposalKind.ExactIdentity);
        Assert.True(exactProposal.HasManualConflict);
        Assert.Contains(JitenCatalogueChoiceAction.Apply, exactProposal.AllowedActions);
        Assert.Contains(JitenCatalogueChoiceAction.Ignore, exactProposal.AllowedActions);
        Assert.Equal("My title", exactProposal.ExistingInstallmentTitle);

        var additionProposal = page.ViewModel.Proposals.Single(p => p.Kind == JitenCatalogueProposalKind.Addition);
        Assert.Contains(JitenCatalogueChoiceAction.AddNew, additionProposal.AllowedActions);
        Assert.Contains(JitenCatalogueChoiceAction.Ignore, additionProposal.AllowedActions);

        // 3. POST Apply commits choices and redirects
        var operationId = page.ViewModel.OperationId!.Value;
        var choices = new List<ProposalChoiceInput>
        {
            new() { ProposalId = exactProposal.ProposalId, Action = JitenCatalogueChoiceAction.Apply },
            new() { ProposalId = additionProposal.ProposalId, Action = JitenCatalogueChoiceAction.AddNew }
        };

        var applyResult = await page.OnPostApplyAsync(
            series.Id,
            page.ViewModel.ReviewId!.Value,
            page.ViewModel.Fingerprint!,
            operationId,
            choices,
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(applyResult);
        Assert.Equal(series.Id, redirect.RouteValues!["seriesId"]);
        Assert.Equal(operationId, redirect.RouteValues["operationId"]);
        Assert.Equal(false, redirect.RouteValues["replayed"]);

        // 4. GET with operation ID shows receipt view
        var receiptGetResult = await page.OnGetAsync(series.Id, operationId, false, CancellationToken.None);
        Assert.IsType<PageResult>(receiptGetResult);
        Assert.Equal(RefreshJitenUiState.Receipt, page.ViewModel.State);
        var receipt = page.ViewModel.Receipt!;
        Assert.Equal(1, receipt.AddedInstallments);
        Assert.Equal(2, receipt.UpdatedInstallments);
        Assert.Equal(1, receipt.LinkedIdentities);
        Assert.Equal(0, receipt.MarkedMissing);
        Assert.False(receipt.IsReplayed);

        // 5. Database invariants check
        database.Context.ChangeTracker.Clear();
        var installments = await database.Context.MediaInstallments
            .Include(i => i.ProviderIdentities)
            .OrderBy(i => i.OrderKey)
            .ToListAsync();
        Assert.Equal(2, installments.Count);
        Assert.Single(await database.Context.MediaWorks.ToListAsync()); // Only 1 work, zero copies created for Vol 2

        var storedVol1 = installments[0];
        Assert.Equal("Provider one", storedVol1.CanonicalTitle);
        Assert.Equal("My title", storedVol1.TitleOverride);
        Assert.Equal(81_234, storedVol1.CharacterCountOverride);
        Assert.Equal(ReleaseState.Released, storedVol1.ReleaseStateOverride);

        var storedCopy = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("https://example.test/protected.jpg", storedCopy.CoverUrl);
        Assert.Equal(MediaCoverSource.Ttsu, storedCopy.CoverSource);

        var storedVol2 = installments[1];
        Assert.Contains(storedVol2.ProviderIdentities, id => id.NormalizedKey == "subdeck:10:102");
        Assert.Equal("https://cdn.jiten.moe/parent.jpg", storedVol2.CanonicalCoverUrl);
        Assert.Equal(CanonicalCoverSource.ProviderParentFallback, storedVol2.CanonicalCoverSource);
    }

    [Fact]
    public async Task AmbiguousAssociation_ShowsCandidates_AndAllowsExplicitLink()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 50 };
        var candidate1 = new MediaInstallment("Same Volume", MediaType.Book, 100)
        {
            MediaSeries = series
        };
        var copy1 = new MediaWork("Copy 1", jitenDeckId: 50, mediaType: MediaType.Book)
        {
            MediaInstallment = candidate1,
            MediaInstallmentId = candidate1.Id,
            MediaSeries = series,
            MediaSeriesId = series.Id
        };
        copy1.LinkToJitenSubdeck(50, 501, 70_000);

        var candidate2 = new MediaInstallment("Same Volume", MediaType.Book, 200)
        {
            MediaSeries = series
        };
        var copy2 = new MediaWork("Copy 2", jitenDeckId: 50, mediaType: MediaType.Book)
        {
            MediaInstallment = candidate2,
            MediaInstallmentId = candidate2.Id,
            MediaSeries = series,
            MediaSeriesId = series.Id
        };
        copy2.LinkToJitenSubdeck(50, 501, 70_000);

        database.Context.AddRange(series, candidate1, copy1, candidate2, copy2);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(50, Deck(501, "Same Volume", 70_000)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var reconciliationService = new JitenCatalogueReconciliationService(database.Context, client, store);
        var page = CreatePageModel(database.Context, reconciliationService, store);

        // Preview
        await page.OnPostPreviewAsync(series.Id, 50, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Reviewing, page.ViewModel.State);
        var proposal = Assert.Single(page.ViewModel.Proposals!);
        Assert.Equal(JitenCatalogueProposalKind.AmbiguousAssociation, proposal.Kind);
        Assert.Equal(2, proposal.Candidates.Count);
        Assert.Contains(candidate1.Id, proposal.Candidates.Select(c => c.Id));
        Assert.Contains(candidate2.Id, proposal.Candidates.Select(c => c.Id));

        // Submit link to candidate 2
        var choices = new List<ProposalChoiceInput>
        {
            new()
            {
                ProposalId = proposal.ProposalId,
                Action = JitenCatalogueChoiceAction.LinkExisting,
                InstallmentId = candidate2.Id
            }
        };

        var applyResult = await page.OnPostApplyAsync(
            series.Id,
            page.ViewModel.ReviewId!.Value,
            page.ViewModel.Fingerprint!,
            Guid.NewGuid(),
            choices,
            CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(applyResult);

        database.Context.ChangeTracker.Clear();
        var refreshedCandidates = await database.Context.MediaInstallments
            .Include(i => i.ProviderIdentities)
            .OrderBy(i => i.OrderKey)
            .ToListAsync();

        Assert.Empty(refreshedCandidates[0].ProviderIdentities);
        Assert.Single(refreshedCandidates[1].ProviderIdentities);
        Assert.Equal("subdeck:50:501", refreshedCandidates[1].ProviderIdentities[0].NormalizedKey);
    }

    [Fact]
    public async Task DuplicateProviderIdentity_And_MissingEntries_FollowAllowedActions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 40 };
        var existing = new MediaInstallment("Volume 2", MediaType.Book, 100)
        {
            MediaSeries = series
        };
        existing.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "subdeck:40:402",
            MediaInstallment = existing,
            ProviderItemId = 402,
            ParentProviderItemId = 40
        });
        database.Context.AddRange(series, existing);
        await database.Context.SaveChangesAsync();

        // Part A: Complete fetch with duplicate 401 and missing 402
        var clientComplete = new StubClient(CompleteFetch(40,
            Deck(401, "Duplicate Title", 50_000),
            Deck(401, "Duplicate Title", 50_000)));
        var storeA = new InMemoryJitenCatalogueReviewStore();
        var serviceA = new JitenCatalogueReconciliationService(database.Context, clientComplete, storeA);
        var pageA = CreatePageModel(database.Context, serviceA, storeA);

        await pageA.OnPostPreviewAsync(series.Id, 40, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Reviewing, pageA.ViewModel.State);

        var dupProposal = pageA.ViewModel.Proposals!.Single(p => p.Kind == JitenCatalogueProposalKind.DuplicateProviderIdentity);
        Assert.Equal([JitenCatalogueChoiceAction.Ignore], dupProposal.AllowedActions);

        var missingProposal = pageA.ViewModel.Proposals!.Single(p => p.Kind == JitenCatalogueProposalKind.MissingProviderEntry);
        Assert.Contains(JitenCatalogueChoiceAction.MarkMissing, missingProposal.AllowedActions);
        Assert.Contains(JitenCatalogueChoiceAction.Ignore, missingProposal.AllowedActions);

        // Part B: Incomplete fetch missing 402 shows AbsenceUnverified with only Ignore
        var clientIncomplete = new StubClient(new JitenDeckCatalogueFetchResult(
            Detail(40, Deck(403, "Volume 3", 60_000)),
            IsComplete: false,
            ExpectedItems: 5,
            RetrievedItems: 1,
            Warning: "Fetch was incomplete"));
        var storeB = new InMemoryJitenCatalogueReviewStore();
        var serviceB = new JitenCatalogueReconciliationService(database.Context, clientIncomplete, storeB);
        var pageB = CreatePageModel(database.Context, serviceB, storeB);

        await pageB.OnPostPreviewAsync(series.Id, 40, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Reviewing, pageB.ViewModel.State);
        Assert.False(pageB.ViewModel.IsCompleteFetch);

        var unverifiedProposal = pageA.ViewModel.Proposals!.Single(p => p.ProviderKey == "subdeck:40:402");
        Assert.Equal(JitenCatalogueProposalKind.MissingProviderEntry, unverifiedProposal.Kind);

        var unverifiedInB = pageB.ViewModel.Proposals!.Single(p => p.Kind == JitenCatalogueProposalKind.AbsenceUnverified);
        Assert.Equal([JitenCatalogueChoiceAction.Ignore], unverifiedInB.AllowedActions);
    }

    [Fact]
    public async Task StaleReview_LocalChangeOrVersionChange_ShowsReviewAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 30 };
        var installment = new MediaInstallment("Volume 1", MediaType.Book, 100)
        {
            MediaSeries = series
        };
        installment.ProviderIdentities.Add(new InstallmentProviderIdentity
        {
            Provider = "jiten",
            NormalizedKey = "subdeck:30:301",
            MediaInstallment = installment,
            ProviderItemId = 301,
            ParentProviderItemId = 30
        });
        database.Context.AddRange(series, installment);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(30, Deck(301, "New Title", 80_000)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var page = CreatePageModel(database.Context, service, store);

        await page.OnPostPreviewAsync(series.Id, 30, CancellationToken.None);
        var reviewId = page.ViewModel.ReviewId!.Value;
        var fingerprint = page.ViewModel.Fingerprint!;
        var proposal = page.ViewModel.Proposals![0];

        // Mutate local installment in DB (version changes)
        installment.Version = Guid.NewGuid();
        installment.CharacterCountOverride = 99_999;
        await database.Context.SaveChangesAsync();

        var choices = new List<ProposalChoiceInput>
        {
            new() { ProposalId = proposal.ProposalId, Action = JitenCatalogueChoiceAction.Apply }
        };

        var applyResult = await page.OnPostApplyAsync(
            series.Id,
            reviewId,
            fingerprint,
            Guid.NewGuid(),
            choices,
            CancellationToken.None);

        Assert.IsType<PageResult>(applyResult);
        Assert.Equal(RefreshJitenUiState.Error, page.ViewModel.State);
        Assert.True(page.ViewModel.IsStaleReview);
        Assert.Contains("local catalogue changed", page.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // Verify no receipt was written
        Assert.Empty(await database.Context.JitenCatalogueRefreshReceipts.ToListAsync());
    }

    [Fact]
    public async Task IdempotentReplay_SameOperationId_ReturnsCommittedReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 20 };
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(20, Deck(201, "Volume 1", 100_000)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var page = CreatePageModel(database.Context, service, store);

        await page.OnPostPreviewAsync(series.Id, 20, CancellationToken.None);
        var operationId = Guid.NewGuid();
        var choices = new List<ProposalChoiceInput>
        {
            new() { ProposalId = page.ViewModel.Proposals![0].ProposalId, Action = JitenCatalogueChoiceAction.AddNew }
        };

        // First apply
        var firstResult = await page.OnPostApplyAsync(
            series.Id,
            page.ViewModel.ReviewId!.Value,
            page.ViewModel.Fingerprint!,
            operationId,
            choices,
            CancellationToken.None);

        var firstRedirect = Assert.IsType<RedirectToPageResult>(firstResult);
        Assert.Equal(false, firstRedirect.RouteValues!["replayed"]);

        // Restart review store (cleared in-memory reviews)
        var freshStore = new InMemoryJitenCatalogueReviewStore();
        var freshReconciliationService = new JitenCatalogueReconciliationService(database.Context, client, freshStore);
        var replayPage = CreatePageModel(database.Context, freshReconciliationService, freshStore);

        // Replay apply with same operation ID
        var replayResult = await replayPage.OnPostApplyAsync(
            series.Id,
            Guid.NewGuid(), // Unknown review ID
            "irrelevant-fingerprint",
            operationId,
            choices,
            CancellationToken.None);

        var replayRedirect = Assert.IsType<RedirectToPageResult>(replayResult);
        Assert.Equal(true, replayRedirect.RouteValues!["replayed"]);

        // Verify DB only has 1 installment and 1 receipt
        Assert.Single(await database.Context.MediaInstallments.ToListAsync());
        Assert.Single(await database.Context.InstallmentProviderIdentities.ToListAsync());
        Assert.Single(await database.Context.JitenCatalogueRefreshReceipts.ToListAsync());
    }

    [Fact]
    public async Task HttpError_And_CancellationHandling()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 60 };
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();

        // 1. Rate limiting error
        var rateLimitClient = new ThrowingClient(new JitenHttpException(
            "Rate limit exceeded",
            HttpStatusCode.TooManyRequests,
            TimeSpan.FromSeconds(45)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var serviceRateLimit = new JitenCatalogueReconciliationService(database.Context, rateLimitClient, store);
        var pageRateLimit = CreatePageModel(database.Context, serviceRateLimit, store);

        await pageRateLimit.OnPostPreviewAsync(series.Id, 60, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Error, pageRateLimit.ViewModel.State);
        Assert.Equal(45, pageRateLimit.ViewModel.RetryAfterSeconds);
        Assert.Contains("rate limit", pageRateLimit.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // 2. Client timeout (cancellation not requested by token)
        var timeoutClient = new ThrowingClient(new OperationCanceledException());
        var serviceTimeout = new JitenCatalogueReconciliationService(database.Context, timeoutClient, store);
        var pageTimeout = CreatePageModel(database.Context, serviceTimeout, store);

        await pageTimeout.OnPostPreviewAsync(series.Id, 60, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Error, pageTimeout.ViewModel.State);
        Assert.Contains("too long to respond", pageTimeout.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // 3. User request aborted cancellation propagates
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await pageTimeout.OnPostPreviewAsync(series.Id, 60, cts.Token));
    }

    [Fact]
    public async Task UnanchoredSeries_RequiresPositiveDeckId_AndNonBookRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unanchoredBook = new MediaSeries("Unanchored Book", MediaType.Book);
        var animeSeries = new MediaSeries("Anime Series", MediaType.Anime);
        database.Context.AddRange(unanchoredBook, animeSeries);
        await database.Context.SaveChangesAsync();

        var client = new StubClient(CompleteFetch(70, Deck(701, "Volume 1", 50_000)));
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(database.Context, client, store);
        var page = CreatePageModel(database.Context, service, store);

        // Non-book series is rejected
        await page.OnGetAsync(animeSeries.Id, null, null, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Error, page.ViewModel.State);
        Assert.Contains("book series only", page.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // Unanchored book with invalid deck ID
        await page.OnPostPreviewAsync(unanchoredBook.Id, 0, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.DeckPrompt, page.ViewModel.State);
        Assert.Contains("valid positive", page.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // Unanchored book with valid deck ID successfully anchors on apply
        await page.OnPostPreviewAsync(unanchoredBook.Id, 70, CancellationToken.None);
        Assert.Equal(RefreshJitenUiState.Reviewing, page.ViewModel.State);

        var choices = new List<ProposalChoiceInput>
        {
            new() { ProposalId = page.ViewModel.Proposals![0].ProposalId, Action = JitenCatalogueChoiceAction.AddNew }
        };

        await page.OnPostApplyAsync(
            unanchoredBook.Id,
            page.ViewModel.ReviewId!.Value,
            page.ViewModel.Fingerprint!,
            Guid.NewGuid(),
            choices,
            CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var anchored = await database.Context.MediaSeries.SingleAsync(s => s.Id == unanchoredBook.Id);
        Assert.Equal(70, anchored.JitenDeckId);
    }

    [Fact]
    public async Task ReceiptDisplay_DoesNotCrossSeriesBoundary()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = new MediaSeries("Owner", MediaType.Book) { JitenDeckId = 80 };
        var other = new MediaSeries("Other", MediaType.Book) { JitenDeckId = 81 };
        database.Context.AddRange(owner, other);
        var operationId = Guid.NewGuid();
        database.Context.JitenCatalogueRefreshReceipts.Add(new JitenCatalogueRefreshReceipt
        {
            Id = operationId,
            MediaSeriesId = owner.Id,
            JitenDeckId = 80,
            ReviewFingerprint = "review",
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();

        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(
            database.Context,
            new StubClient(CompleteFetch(81, Deck(811, "Volume", 70_000))),
            store);
        var page = CreatePageModel(database.Context, service, store);

        var result = await page.OnGetAsync(other.Id, operationId, true, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(RefreshJitenUiState.Error, page.ViewModel.State);
        Assert.Null(page.ViewModel.Receipt);
        Assert.Contains("not found for this series", page.ViewModel.ErrorMessage!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingOperationCorrelation_PreservesTheActiveReviewForCorrection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var series = new MediaSeries("Series", MediaType.Book) { JitenDeckId = 82 };
        database.Context.Add(series);
        await database.Context.SaveChangesAsync();
        var store = new InMemoryJitenCatalogueReviewStore();
        var service = new JitenCatalogueReconciliationService(
            database.Context,
            new StubClient(CompleteFetch(82, Deck(821, "Volume", 70_000))),
            store);
        var page = CreatePageModel(database.Context, service, store);
        await page.OnPostPreviewAsync(series.Id, 82, CancellationToken.None);
        var proposal = Assert.Single(page.ViewModel.Proposals!);

        var result = await page.OnPostApplyAsync(
            series.Id,
            page.ViewModel.ReviewId!.Value,
            page.ViewModel.Fingerprint!,
            Guid.Empty,
            [new ProposalChoiceInput
            {
                ProposalId = proposal.ProposalId,
                Action = proposal.RecommendedAction
            }],
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(RefreshJitenUiState.Reviewing, page.ViewModel.State);
        Assert.Contains("operation ID", page.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await database.Context.JitenCatalogueRefreshReceipts.ToListAsync());
    }

    private static RefreshJitenModel CreatePageModel(
        ImmersionDbContext context,
        JitenCatalogueReconciliationService reconciliationService,
        IJitenCatalogueReviewStore store)
    {
        _ = store;
        var httpContext = new DefaultHttpContext();
        var tempDataProvider = new TestTempDataProvider();
        var model = new RefreshJitenModel(
            context,
            reconciliationService,
            NullLogger<RefreshJitenModel>.Instance)
        {
            TempData = new TempDataDictionary(httpContext, tempDataProvider),
            PageContext = new PageContext { HttpContext = httpContext }
        };
        return model;
    }

    private static JitenDeckDTO Deck(int id, string title, int characters, string cover = "") => new()
    {
        DeckId = id,
        ParentDeckId = null,
        OriginalTitle = title,
        CharacterCount = characters,
        CoverName = cover
    };

    private static JitenDeckDetailDTO Detail(int parentId, params JitenDeckDTO[] children) => new()
    {
        MainDeck = new JitenDeckDTO
        {
            DeckId = parentId,
            OriginalTitle = "Parent",
            ChildrenDeckCount = children.Length,
            CoverName = "https://cdn.jiten.moe/parent.jpg"
        },
        SubDecks = children.ToList()
    };

    private static JitenDeckCatalogueFetchResult CompleteFetch(int parentId, params JitenDeckDTO[] children) =>
        new(Detail(parentId, children), true, children.Length, children.Length);

    private sealed class StubClient(JitenDeckCatalogueFetchResult fetch) : IJitenApiClient
    {
        public JitenDeckCatalogueFetchResult Fetch { get; set; } = fetch;

        public Task<JitenDeckCatalogueFetchResult> GetDeckCatalogueAsync(int deckId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Fetch);
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId,
            CancellationToken cancellationToken = default) => Task.FromResult(Fetch.Detail);

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId,
            CancellationToken cancellationToken = default) => Task.FromResult<JitenFranchiseDTO?>(null);
    }

    private sealed class ThrowingClient(Exception exception) : IJitenApiClient
    {
        public Task<JitenDeckCatalogueFetchResult> GetDeckCatalogueAsync(int deckId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query,
            CancellationToken cancellationToken = default) => throw exception;

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId,
            CancellationToken cancellationToken = default) => throw exception;
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
