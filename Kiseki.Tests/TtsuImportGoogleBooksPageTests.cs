using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Covers;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Core.Services.Metadata;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Import;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Tests;

public sealed class TtsuImportGoogleBooksPageTests
{
    [Fact]
    public async Task Preview_WhenHighConfidenceCandidateHasNoJitenCover_EnrichesWithGoogleBooksCover()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "vol_google_123",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=vol_google_123",
                evidence: []))
        };

        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        Assert.IsType<PageResult>(result);
        Assert.True(model.HasPreview);
        var book = Assert.Single(model.Books);
        Assert.NotNull(book.Enrichment);
        var topChoice = Assert.Single(book.Enrichment.Candidates);
        Assert.True(topChoice.HasGoogleCover);
        Assert.Equal("vol_google_123", topChoice.GoogleVolumeId);
        Assert.Equal("https://books.google.com/books?id=vol_google_123", topChoice.GoogleAttributionLink);
        Assert.Equal("https://books.google.com/cover.jpg", topChoice.CoverUrl);
        Assert.Single(googleService.ResolveInvocations);
    }

    [Fact]
    public async Task Preview_WhenCandidateHasJitenSeriesFallback_UpgradesPreviewWithGoogleBooks()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(
            deckId: 10,
            title: "Test Book",
            coverUrl: "https://cdn.jiten.moe/deck.jpg",
            coverEvidence: JitenCoverEvidence.ParentFallback);

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "vol_google_123",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=vol_google_123",
                evidence: []))
        };
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        Assert.IsType<PageResult>(result);
        var book = Assert.Single(model.Books);
        Assert.NotNull(book.Enrichment);
        var topChoice = Assert.Single(book.Enrichment.Candidates);
        Assert.True(topChoice.HasGoogleCover);
        Assert.Equal("https://books.google.com/cover.jpg", topChoice.CoverUrl);
        Assert.Single(googleService.ResolveInvocations);
    }

    [Fact]
    public async Task Review_AfterNoMatch_DoesNotRepeatGoogleBooksLookup()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };
        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "No exact volume match."))
        };
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);
        Assert.Equal("No exact volume match.", model.Books.Single().Enrichment!.Candidates.Single().GoogleCoverWarning);
        Assert.Single(googleService.ResolveInvocations);

        await model.OnPostReviewAsync(CancellationToken.None);

        Assert.Equal("No exact volume match.", model.Books.Single().Enrichment!.Candidates.Single().GoogleCoverWarning);
        Assert.Single(googleService.ResolveInvocations);
    }

    [Fact]
    public async Task EnrichNext_CallerCancellationDuringGoogleLookup_Propagates()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        using var cancellation = new CancellationTokenSource();

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };
        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(GoogleBooksCoverMatchResult.CreateNoMatch());
            }
        };
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => model.OnPostEnrichNextAsync(model.BatchId, cancellation.Token));
    }

    [Fact]
    public async Task ReviewCandidate_IsResolvedOnlyAfterExplicitSelectionAndRefresh()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.Review, CreateScored(candidate, 80))
            ])
        };
        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "reviewed_volume",
                "https://books.google.com/reviewed.jpg",
                "https://books.google.com/books?id=reviewed_volume",
                evidence: ["Exact reviewed candidate"]))
        };
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        Assert.Empty(googleService.ResolveInvocations);
        Assert.Null(model.Selections.Single().CandidateKey);

        model.Selections.Single().CandidateKey = model.Books.Single().Enrichment!.Candidates.Single().Key;
        await model.OnPostReviewAsync(CancellationToken.None);

        Assert.Single(googleService.ResolveInvocations);
        Assert.True(model.Books.Single().Enrichment!.Candidates.Single().HasGoogleCover);
    }

    [Fact]
    public async Task Preview_WhenAutoMatchMetadataIsDisabled_MakesNoGoogleRequest()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var googleService = new StubGoogleBooksCoverService();
        var model = CreateModel(database.Context, googleCoverService: googleService);
        model.AutoMatchMetadata = false;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.Empty(googleService.ResolveInvocations);
    }

    [Fact]
    public async Task Preview_AmbiguousGoogleMatch_KeepsJitenFallbackAndShowsWarning()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var candidate = CreateCandidate(
            deckId: 10,
            title: "Test Book",
            coverUrl: "https://cdn.jiten.moe/fallback.jpg",
            coverEvidence: JitenCoverEvidence.ParentFallback);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };
        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateAmbiguous(
                warning: "Multiple exact volumes were returned."))
        };
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        var previewCandidate = model.Books.Single().Enrichment!.Candidates.Single();
        Assert.False(previewCandidate.HasGoogleCover);
        Assert.Equal("https://cdn.jiten.moe/fallback.jpg", previewCandidate.CoverUrl);
        Assert.Equal("Multiple exact volumes were returned.", previewCandidate.GoogleCoverWarning);
    }

    [Fact]
    public async Task Preview_WhenTargetWorkHasProtectedCover_DoesNotCallGoogleBooks()
    {
        await using var database = await TestDatabase.CreateAsync();
        var existing = new MediaWork("Test Book");
        existing.UpdateCoverUrl("https://example.com/protected.jpg");
        database.Context.MediaWorks.Add(existing);
        await database.Context.SaveChangesAsync();

        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService();
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        Assert.Empty(googleService.ResolveInvocations);
    }

    [Fact]
    public async Task Confirm_AppliesVerifiedGoogleBooksCover_ToNewMediaWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Original", "Romaji", "English", 18_610, null, 0, JitenCoverEvidence.None)))
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "vol_google_123",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=vol_google_123",
                evidence: [])),
            VerifyHandler = (volId, ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                volId,
                "https://books.google.com/cover.jpg",
                $"https://books.google.com/books?id={volId}",
                evidence: []))
        };

        var model = CreateModel(database.Context, matchService, resolver, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);
        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.Equal(MediaCoverSource.GoogleBooks, work.CoverSource);
        Assert.Equal("https://books.google.com/cover.jpg", work.CoverUrl);
        Assert.Equal("vol_google_123", work.CoverProviderItemId);
        Assert.Equal(10, work.JitenDeckId);
        Assert.Single(googleService.VerifyInvocations);
    }

    [Fact]
    public async Task Confirm_WhenGoogleCoverVerificationFails_ImportsBookWithoutFailingTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Original", "Romaji", "English", 18_610, null, 0, JitenCoverEvidence.None)))
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "vol_google_123",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=vol_google_123",
                evidence: [])),
            // Verification fails during confirm (e.g. image removed or aspect ratio failed)
            VerifyHandler = (volId, ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateInvalidImage(
                "Cover image was no longer valid."))
        };

        var model = CreateModel(database.Context, matchService, resolver, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);
        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks
            .Include(w => w.Logs)
            .SingleAsync();
        Assert.Equal("Test Book", work.Title);
        // Jiten link applied, but no cover
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
        Assert.Null(work.CoverUrl);
        Assert.Null(work.CoverProviderItemId);
        Assert.Equal(2, work.Logs.Count);
    }

    [Fact]
    public async Task Confirm_ReplayReceipt_ReturnsCommittedReceiptWithoutCallingGoogleBooksAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Original", "Romaji", "English", 18_610, null, 0, JitenCoverEvidence.None)))
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "vol_google_123",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=vol_google_123",
                evidence: [])),
            VerifyHandler = (volId, ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                volId,
                "https://books.google.com/cover.jpg",
                $"https://books.google.com/books?id={volId}",
                evidence: []))
        };

        var cache = new MemoryCache(new MemoryCacheOptions());
        var batchStore = new TtsuImportBatchStore(cache);

        var model = CreateModel(database.Context, matchService, resolver, batchStore, googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);
        var firstResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(firstResult);

        Assert.Single(googleService.VerifyInvocations);

        // Replaying same batch id
        var replayedModel = CreateModel(database.Context, matchService, resolver, batchStore, googleService);
        replayedModel.BatchId = model.BatchId;
        var replayResult = await replayedModel.OnPostConfirmAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(replayResult);
        // Verify was not called again
        Assert.Single(googleService.VerifyInvocations);
    }

    [Fact]
    public async Task EnrichNext_InternalGoogleBudgetTimeout_RecordsTimedOutStatusAndCompletesBook()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (_, ct) =>
            {
                // Simulate internal timeout by throwing OperationCanceledException with an expired token
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };

        var batchStore = new TtsuImportBatchStore(new MemoryCache(new MemoryCacheOptions()));
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService, batchStore: batchStore);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        var enrichResult = await model.OnPostEnrichNextAsync(model.BatchId, CancellationToken.None);

        Assert.IsType<JsonResult>(enrichResult);
        Assert.True(batchStore.TryGet(model.BatchId, out var batch));
        var batchBook = Assert.Single(batch!.Books);
        Assert.Equal(TtsuEnrichmentAttemptState.Completed, batchBook.EnrichmentState);
        var topChoice = Assert.Single(batchBook.Enrichment!.Candidates);
        Assert.Equal(GoogleBooksMatchStatus.TimedOut, topChoice.GoogleCoverStatus);
        Assert.Equal("Google Books lookup timed out.", topChoice.GoogleCoverWarning);

        // In review page model:
        await model.OnPostReviewAsync(CancellationToken.None);
        var reviewChoice = Assert.Single(model.Books[0].Enrichment!.Candidates);
        Assert.Equal("Google Books lookup timed out", reviewChoice.GoogleStatusMessage);
    }

    [Fact]
    public async Task EnrichNext_CallerCancellation_ResetsBookToPendingAndRethrows()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var batchStore = new TtsuImportBatchStore(new MemoryCache(new MemoryCacheOptions()));
        var googleService = new StubGoogleBooksCoverService();
        var model = CreateModel(database.Context, matchService, googleCoverService: googleService, batchStore: batchStore);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            model.OnPostEnrichNextAsync(model.BatchId, callerCts.Token));

        Assert.True(batchStore.TryGet(model.BatchId, out var batch));
        var batchBook = Assert.Single(batch!.Books);
        Assert.Equal(TtsuEnrichmentAttemptState.Pending, batchBook.EnrichmentState);
    }

    [Fact]
    public async Task Preview_CandidateUiCap_AtMostThreeCandidatesRendered_AndSummariesComputed()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var scoredList = Enumerable.Range(1, 6).Select(i => new ScoredCandidate
        {
            Candidate = new JitenMatchCandidate
            {
                DeckId = 100,
                SubdeckId = 100 + i,
                OriginalTitle = $"Volume {i}",
                ParentOriginalTitle = "Series",
                CharacterCount = 50_000,
                CoverEvidence = JitenCoverEvidence.Specific
            },
            TotalScore = 80 - i,
            TitleScore = 60,
            VolumeScore = 25,
            CandidateVolume = StructuredVolume.Standard(1),
            Evidence = ["Matched"]
        }).ToList();

        var parsed = new ParsedMediaTitle
        {
            OriginalTitle = "Series 1",
            ComparisonTitle = "series 1",
            BaseTitle = "Series",
            Volume = StructuredVolume.Standard(1)
        };

        var outcome = JitenMatchOutcome.Matched(
            Guid.Empty,
            new JitenMatchResult
            {
                ParsedTitle = parsed,
                Confidence = MatchConfidence.Review,
                Candidates = scoredList,
                FilteredIncompatibleCount = 44,
                Evidence = ["Review"]
            });

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                outcome with { CorrelationId = reqs[0].CorrelationId }
            ])
        };

        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        var enrichment = model.Books[0].Enrichment!;
        Assert.Equal(3, enrichment.Candidates.Count); // Capped at 3
        Assert.Equal(3, enrichment.OmittedPlausibleCount); // 6 - 3 = 3
        Assert.Equal(44, enrichment.FilteredIncompatibleCount);
    }

    [Fact]
    public async Task Preview_UnconfiguredGoogleBooks_ReflectedInModelAndCandidateStatus()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var unconfiguredGoogleService = new StubGoogleBooksCoverService { IsConfigured = false };
        var model = CreateModel(database.Context, matchService, googleCoverService: unconfiguredGoogleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        Assert.False(model.IsGoogleBooksConfigured);
        var choice = Assert.Single(model.Books[0].Enrichment!.Candidates);
        Assert.Equal("Google Books is not configured", choice.GoogleStatusMessage);
    }

    [Fact]
    public async Task Preview_InferredGoogleCover_AttachesToCorrectCandidate_WithoutAutoSelectingReviewCandidate()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.Review, CreateScored(candidate, 80))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "d0Qj0AEACAAJ",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=d0Qj0AEACAAJ",
                evidence: ["Volume inferred from two matching searches"],
                proof: GoogleBooksIdentityProof.CrossQueryInferredVolume,
                isLowResolution: true))
        };

        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        // Review confidence does NOT auto-select candidate
        Assert.Null(model.Selections.Single().CandidateKey);

        // User explicitly selects candidate and reviews
        model.Selections.Single().CandidateKey = model.Books.Single().Enrichment!.Candidates.Single().Key;
        await model.OnPostReviewAsync(CancellationToken.None);

        var choice = Assert.Single(model.Books.Single().Enrichment!.Candidates);
        Assert.True(choice.HasGoogleCover);
        Assert.Equal(GoogleBooksIdentityProof.CrossQueryInferredVolume, choice.GoogleProof);
        Assert.True(choice.IsGoogleLowResolution);
        Assert.Equal("Low-resolution Google Books cover", choice.GoogleStatusMessage);
        Assert.Equal("Low-resolution Google Books cover", choice.CoverEvidenceLabel);
    }

    [Fact]
    public async Task Preview_InferredGoogleCover_NormalResolution_StatusMessageSaysInferred()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateMatched(
                "d0Qj0AEACAAJ",
                "https://books.google.com/cover.jpg",
                "https://books.google.com/books?id=d0Qj0AEACAAJ",
                evidence: ["Volume inferred from two matching searches"],
                proof: GoogleBooksIdentityProof.CrossQueryInferredVolume,
                isLowResolution: false))
        };

        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        var choice = Assert.Single(model.Books.Single().Enrichment!.Candidates);
        Assert.True(choice.HasGoogleCover);
        Assert.Equal(GoogleBooksIdentityProof.CrossQueryInferredVolume, choice.GoogleProof);
        Assert.False(choice.IsGoogleLowResolution);
        Assert.Equal("Google Books cover found — volume inferred from two matching searches", choice.GoogleStatusMessage);
        Assert.Equal("Google Books volume cover", choice.CoverEvidenceLabel);
    }

    [Fact]
    public async Task Preview_MarkerlessAmbiguous_StatusMessageSaysMultipleMarkerlessEditionsRemainedAmbiguous()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateAmbiguous(
                warning: "Google Books checked — multiple markerless editions remained ambiguous"))
        };

        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        var choice = Assert.Single(model.Books.Single().Enrichment!.Candidates);
        Assert.Equal("Google Books checked — multiple markerless editions remained ambiguous", choice.GoogleStatusMessage);
    }

    [Fact]
    public async Task Preview_MarkerlessInsufficientProof_StatusMessageSaysCrossQueryProofWasInsufficient()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(deckId: 10, title: "Test Book", coverEvidence: JitenCoverEvidence.None);
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
            [
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var googleService = new StubGoogleBooksCoverService
        {
            ResolveHandler = (ctx, _) => Task.FromResult(GoogleBooksCoverMatchResult.CreateNoMatch(
                warning: "Google Books checked — exact title found, but Google omitted the volume number and cross-query proof was insufficient"))
        };

        var model = CreateModel(database.Context, matchService, googleCoverService: googleService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);
        await EnrichAllPendingAsync(model);

        var choice = Assert.Single(model.Books.Single().Enrichment!.Candidates);
        Assert.Equal("Google Books checked — exact title found, but Google omitted the volume number and cross-query proof was insufficient", choice.GoogleStatusMessage);
    }

    private static JitenMatchCandidate CreateCandidate(
        int deckId = 1,
        string title = "Test Book",
        int characters = 100_000,
        string? coverUrl = null,
        JitenCoverEvidence coverEvidence = JitenCoverEvidence.None,
        int? subdeckId = null) =>
        new()
        {
            DeckId = deckId,
            SubdeckId = subdeckId,
            OriginalTitle = title,
            CharacterCount = characters,
            CoverUrl = coverUrl,
            CoverEvidence = coverEvidence
        };

    private static ScoredCandidate CreateScored(
        JitenMatchCandidate candidate,
        int score = 100) =>
        new()
        {
            Candidate = candidate,
            TotalScore = score,
            TitleScore = 50,
            VolumeScore = 30,
            CharacterCountScore = 20,
            MatchedTitle = MatchedTitleVariant.Original,
            Evidence = ["Exact title match"]
        };

    private static JitenMatchOutcome CreateMatchedOutcome(
        Guid correlationId,
        MatchConfidence confidence,
        params ScoredCandidate[] candidates)
    {
        var parsed = new ParsedMediaTitle
        {
            OriginalTitle = "Test Book",
            ComparisonTitle = "test book",
            BaseTitle = "Test Book"
        };
        var result = new JitenMatchResult
        {
            ParsedTitle = parsed,
            Confidence = confidence,
            Candidates = candidates,
            Evidence = ["Confidence: " + confidence]
        };
        return JitenMatchOutcome.Matched(correlationId, result);
    }

    private static TtsuModel CreateModel(
        ImmersionDbContext context,
        StubJitenMatchService? matchService = null,
        IJitenSelectionResolver? selectionResolver = null,
        ITtsuImportBatchStore? batchStore = null,
        IGoogleBooksCoverService? googleCoverService = null)
    {
        var httpContext = new DefaultHttpContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var model = new TtsuModel(
            new TtsuDataLoader(),
            batchStore ?? new TtsuImportBatchStore(cache),
            context,
            matchService ?? new StubJitenMatchService(),
            selectionResolver ?? new StubJitenSelectionResolver(),
            googleCoverService)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };

        return model;
    }

    private static async Task EnrichAllPendingAsync(TtsuModel model, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await model.OnPostEnrichNextAsync(model.BatchId, cancellationToken);
            if (result is JsonResult json && json.Value is not null)
            {
                var completeProp = json.Value.GetType().GetProperty("complete");
                if (completeProp?.GetValue(json.Value) is true)
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        await model.OnPostReviewAsync(cancellationToken, fromEnrichment: true);
    }

    private static FormFile StatisticsFile(Stream stream, string folderTitle = "Test Book")
    {
        return new FormFile(
            stream,
            0,
            stream.Length,
            "FolderFiles",
            $"ttu-reader-data/{folderTitle}/statistics.json");
    }

    private static string GetFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _values = new Dictionary<string, object>(values);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, ImmersionDbContext context)
        {
            _connection = connection;
            Context = context;
        }

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

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubJitenMatchService : IJitenMatchService
    {
        public Func<IReadOnlyList<JitenMatchRequest>, TimeSpan, CancellationToken, Task<IReadOnlyList<JitenMatchOutcome>>>? Handler { get; set; }

        public Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
            IReadOnlyList<JitenMatchRequest> requests,
            TimeSpan timeBudget,
            CancellationToken cancellationToken = default)
        {
            if (Handler is not null)
            {
                return Handler(requests, timeBudget, cancellationToken);
            }

            return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                requests.Select(r => JitenMatchOutcome.NoCandidates(r.CorrelationId)).ToList());
        }
    }

    private sealed class StubJitenSelectionResolver : IJitenSelectionResolver
    {
        public Func<int, int?, CancellationToken, Task<JitenSelectionResult>>? Handler { get; set; }
        public List<(int DeckId, int? SubdeckId)> Invocations { get; } = [];

        public Task<JitenSelectionResult> ResolveAsync(
            int parentDeckId,
            int? subdeckId = null,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((parentDeckId, subdeckId));
            if (Handler is not null)
            {
                return Handler(parentDeckId, subdeckId, cancellationToken);
            }

            return Task.FromResult(JitenSelectionResult.Failed(
                JitenSelectionStatus.DeckNotFound,
                "No stub handler configured"));
        }

        public JitenSelectionResult Resolve(
            JitenDeckDetailDTO detail,
            int parentDeckId,
            int? subdeckId = null)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.DeckNotFound,
                "Not supported in stub");
        }
    }

    internal sealed class StubGoogleBooksCoverService : IGoogleBooksCoverService
    {
        public bool IsConfigured { get; set; } = true;
        public Func<GoogleCoverLookupContext, CancellationToken, Task<GoogleBooksCoverMatchResult>>? ResolveHandler { get; set; }
        public Func<string, GoogleCoverLookupContext, CancellationToken, Task<GoogleBooksCoverMatchResult>>? VerifyHandler { get; set; }

        public List<GoogleCoverLookupContext> ResolveInvocations { get; } = [];
        public List<(string VolumeId, GoogleCoverLookupContext Context)> VerifyInvocations { get; } = [];

        public Task<GoogleBooksCoverMatchResult> ResolveCoverAsync(
            GoogleCoverLookupContext context,
            CancellationToken cancellationToken = default)
        {
            ResolveInvocations.Add(context);
            if (!IsConfigured)
            {
                return Task.FromResult(GoogleBooksCoverMatchResult.CreateNotConfigured());
            }

            if (ResolveHandler is not null)
            {
                return ResolveHandler(context, cancellationToken);
            }

            return Task.FromResult(GoogleBooksCoverMatchResult.CreateNoMatch(
                evidence: [],
                warning: "No handler"));
        }

        public Func<ExternalCoverProvider, string, GoogleCoverLookupContext, CancellationToken, Task<GoogleBooksCoverMatchResult>>? VerifyCoverHandler { get; set; }
        public List<(ExternalCoverProvider Provider, string ProviderItemId, GoogleCoverLookupContext Context)> VerifyCoverInvocations { get; } = [];

        public Task<GoogleBooksCoverMatchResult> VerifyVolumeCoverAsync(
            string volumeId,
            GoogleCoverLookupContext context,
            CancellationToken cancellationToken = default)
        {
            VerifyInvocations.Add((volumeId, context));
            if (VerifyHandler is not null)
            {
                return VerifyHandler(volumeId, context, cancellationToken);
            }

            return Task.FromResult(GoogleBooksCoverMatchResult.CreateNoMatch(
                evidence: [],
                warning: "No handler"));
        }

        public Task<GoogleBooksCoverMatchResult> VerifyCoverAsync(
            ExternalCoverProvider provider,
            string providerItemId,
            GoogleCoverLookupContext context,
            CancellationToken cancellationToken = default)
        {
            VerifyCoverInvocations.Add((provider, providerItemId, context));
            if (VerifyCoverHandler is not null)
            {
                return VerifyCoverHandler(provider, providerItemId, context, cancellationToken);
            }

            return VerifyVolumeCoverAsync(providerItemId, context, cancellationToken);
        }
    }
}
