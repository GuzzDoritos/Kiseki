using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;
using Kiseki.Web.Models;
using Kiseki.Web.Pages.Import;
using Kiseki.Web.Services;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kiseki.Tests;

public sealed class TtsuImportPageTests
{
    [Fact]
    public async Task Preview_ParsesBooksWithoutWritingToDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.HasPreview);
        Assert.Single(model.Books);
        Assert.Equal("Test Book", model.Books[0].Title);
        Assert.Equal(18_610, model.Books[0].CharactersRead);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ImportsTheSelectedBookAndSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);
        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks
            .Include(mediaWork => mediaWork.Logs)
            .SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.Equal(2, work.Logs.Count);
        Assert.Equal(18_610, work.CurrentCharactersRead);
    }

    [Fact]
    public async Task Confirm_ReimportUpdatesMatchingTtsuDates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var existingBook = new TtsuBookContainer
        {
            Title = "test book",
            Entries =
            [
                new TtsuReaderDTO
                {
                    Title = "test book",
                    DateKey = "2026-08-05",
                    CharactersRead = 1,
                    ReadingTime = 1,
                    LastStatisticModified = 1
                }
            ]
        };
        database.Context.MediaWorks.Add(TtsuBookImporter.CreateMediaWork(existingBook));
        await database.Context.SaveChangesAsync();

        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.True(model.Books.Single().ExistsInLibrary);
        database.Context.ChangeTracker.Clear();
        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var works = await database.Context.MediaWorks
            .Include(mediaWork => mediaWork.Logs)
            .ToListAsync();
        Assert.Single(works);
        Assert.Equal(2, works[0].Logs.Count);
        Assert.Equal(18_610, works[0].CurrentCharactersRead);
    }

    [Fact]
    public async Task Preview_RejectsAFolderWithoutStatisticsFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var unrelatedStream = new MemoryStream([1, 2, 3]);
        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            new FormFile(unrelatedStream, 0, unrelatedStream.Length, "FolderFiles", "cover.png")
        ];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.False(model.HasPreview);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ImportsOnlySelectedBooksFromAMultiBookPreview()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));
        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            StatisticsFile(firstStream, "Test Book"),
            StatisticsFile(secondStream, "Second Book")
        ];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.Equal(2, model.Books.Count);
        var secondBookIndex = model.Books
            .Select((book, index) => new { book.Title, Index = index })
            .Single(item => item.Title == "Second Book")
            .Index;
        model.Selections[secondBookIndex].Selected = false;

        await model.OnPostConfirmAsync(CancellationToken.None);

        var importedWork = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("Test Book", importedWork.Title);
    }

    [Fact]
    public async Task Confirm_RejectsAnExpiredBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var model = CreateModel(database.Context);
        model.BatchId = Guid.NewGuid();

        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.False(model.HasPreview);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Confirm_LegacyDifferenceRequiresResolutionAndRefreshedReview()
    {
        await using var database = await TestDatabase.CreateAsync();
        var work = new Kiseki.Core.Entities.MediaWork("Test Book");
        work.Logs.Add(new() { Date = new(2026, 8, 5), CharactersRead = 1 });
        database.Context.Add(work);
        await database.Context.SaveChangesAsync();
        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        Assert.False(model.Books.Single().Plan.CanApply);
        model.Selections[0].Days[0].Choice = "incoming:0";
        // The new decision has not yet been shown in a reviewed preview.
        Assert.IsType<PageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        database.Context.ChangeTracker.Clear();
        Assert.Equal(1, (await database.Context.ImmersionLogs.SingleAsync()).CharactersRead);
        model.ModelState.Clear();
        Assert.IsType<RedirectToPageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        Assert.Equal(2, await database.Context.ImmersionLogs.CountAsync());
    }

    [Fact]
    public async Task Confirm_RejectsAmbiguousTargetsAndInvalidModes()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.AddRange(new Kiseki.Core.Entities.MediaWork("Test Book"), new Kiseki.Core.Entities.MediaWork("test book"));
        await database.Context.SaveChangesAsync();
        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        Assert.Null(model.Selections.Single().TargetId);
        Assert.IsType<PageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        model.ModelState.Clear();
        model.Selections[0].Mode = (Kiseki.Web.Models.TtsuImportMode)99;
        Assert.IsType<PageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        Assert.Empty(await database.Context.ImmersionLogs.ToListAsync());
    }

    [Fact]
    public async Task Confirm_TargetChangeRequiresReviewAndUsesExplicitId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var target = new Kiseki.Core.Entities.MediaWork("Renamed book");
        database.Context.Add(target);
        await database.Context.SaveChangesAsync();
        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        model.Selections[0].Mode = Kiseki.Web.Models.TtsuImportMode.Merge;
        model.Selections[0].TargetId = target.Id;
        Assert.IsType<PageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        Assert.Empty(await database.Context.ImmersionLogs.ToListAsync());
        model.ModelState.Clear();
        Assert.IsType<RedirectToPageResult>(await model.OnPostConfirmAsync(CancellationToken.None));
        Assert.Equal(target.Id, (await database.Context.MediaWorks.SingleAsync()).Id);
        Assert.Equal("Renamed book", (await database.Context.MediaWorks.SingleAsync()).Title);
    }

    [Fact]
    public async Task Confirm_ReplayedPostAfterPreviewRemovalReturnsReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixture = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(fixture)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        await model.OnPostConfirmAsync(CancellationToken.None);
        var anotherRequest = CreateModel(database.Context);
        anotherRequest.BatchId = model.BatchId;
        Assert.IsType<RedirectToPageResult>(await anotherRequest.OnPostConfirmAsync(CancellationToken.None));
        Assert.Single(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Preview_MultipleFilesUseDailyUnionAndMatchCommittedTotals()
    {
        await using var database = await TestDatabase.CreateAsync();
        var source = await File.ReadAllTextAsync(GetFixturePath());
        using var first = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var second = new MemoryStream(Encoding.UTF8.GetBytes(source.Replace("2026-08-06", "2026-08-07")));
        var model = CreateModel(database.Context);
        model.FolderFiles = [StatisticsFile(first), StatisticsFile(second)];
        await model.OnPostPreviewAsync(CancellationToken.None);
        var preview = Assert.Single(model.Books);
        Assert.Equal(3, preview.Plan.Days.Count);
        await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal(preview.CharactersRead, await database.Context.ImmersionLogs.SumAsync(x => (long)x.CharactersRead));
    }

    [Fact]
    public async Task PreviewAndConfirm_ImportProgressFromTheSameBookFolder()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var statistics = File.OpenRead(GetFixturePath());
        using var progress = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"dataId\":7,\"exploredCharCount\":25000,\"progress\":0.25,\"lastBookmarkModified\":1234}"));
        var model = CreateModel(database.Context);
        model.FolderFiles =
        [
            StatisticsFile(statistics),
            ProgressFile(progress)
        ];

        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = Assert.Single(model.Books);
        Assert.Equal(25_000, preview.CurrentPosition);
        Assert.Equal(100_000, preview.Plan.Progress.ResultingTotalCharacters);
        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(100_000, (await database.Context.MediaWorks.SingleAsync()).TtsuCharacterCount);
        Assert.Equal(25_000, (await database.Context.TtsuBindings.SingleAsync()).CurrentCharacterPosition);
    }

    [Fact]
    public async Task Preview_AutoMatchDisabled_MakesZeroMatchCallsAndPreservesBehavior()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService();
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = false;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Empty(matchService.Invocations);
        Assert.Null(model.Books.Single().Enrichment);
        Assert.Null(model.Selections.Single().CandidateKey);
    }

    [Fact]
    public async Task Preview_AutoMatchEnabled_SendsOneBatchCallWithCorrectCorrelationIdsAndTitles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));
        var matchService = new StubJitenMatchService();
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles =
        [
            StatisticsFile(firstStream, "Test Book"),
            StatisticsFile(secondStream, "Second Book")
        ];

        var result = await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Single(matchService.Invocations);
        var requests = matchService.Invocations[0];
        Assert.Equal(2, requests.Count);

        var firstBook = model.Books.Single(b => b.Title == "Test Book");
        var secondBook = model.Books.Single(b => b.Title == "Second Book");
        Assert.Contains(requests, r => r.CorrelationId == firstBook.BookKey && r.RawTitle == "Test Book");
        Assert.Contains(requests, r => r.CorrelationId == secondBook.BookKey && r.RawTitle == "Second Book");
    }

    [Fact]
    public void ResolveAuthoritativeTtsuTotal_ExactRatioAndCompletionAdjustedAreEligible()
    {
        var bookExact = new TtsuBookContainer
        {
            Title = "Exact Ratio Book",
            ProgressEntries =
            [
                new TtsuProgressDTO
                {
                    ExploredCharacterCount = 25_000,
                    Progress = System.Text.Json.JsonDocument.Parse("0.25").RootElement,
                    LastBookmarkModified = 100
                }
            ]
        };
        var totalExact = TtsuProgressNormalizer.ResolveAuthoritativeTotal(bookExact);
        Assert.Equal(100_000, totalExact);

        var bookCompletion = new TtsuBookContainer
        {
            Title = "Completion Adjusted Book",
            ProgressEntries =
            [
                new TtsuProgressDTO
                {
                    ExploredCharacterCount = 50_000,
                    Progress = System.Text.Json.JsonDocument.Parse("1.0").RootElement,
                    LastBookmarkModified = 100
                }
            ]
        };
        var totalCompletion = TtsuProgressNormalizer.ResolveAuthoritativeTotal(bookCompletion);
        Assert.Equal(50_001, totalCompletion);
    }

    [Fact]
    public void ResolveAuthoritativeTtsuTotal_RoundedPercentageConflictingMissingAndDailyTotalsReturnNull()
    {
        // Rounded percentage string "25%"
        var bookRounded = new TtsuBookContainer
        {
            Title = "Rounded Book",
            ProgressEntries =
            [
                new TtsuProgressDTO
                {
                    ExploredCharacterCount = 25_000,
                    Progress = System.Text.Json.JsonDocument.Parse("\"25%\"").RootElement,
                    LastBookmarkModified = 100
                }
            ]
        };
        Assert.Null(TtsuProgressNormalizer.ResolveAuthoritativeTotal(bookRounded));

        // Conflicting inferred totals from same revision
        var bookConflicting = new TtsuBookContainer
        {
            Title = "Conflicting Book",
            ProgressEntries =
            [
                new TtsuProgressDTO
                {
                    ExploredCharacterCount = 25_000,
                    Progress = System.Text.Json.JsonDocument.Parse("0.25").RootElement,
                    LastBookmarkModified = 100
                },
                new TtsuProgressDTO
                {
                    ExploredCharacterCount = 24_000,
                    Progress = System.Text.Json.JsonDocument.Parse("0.20").RootElement,
                    LastBookmarkModified = 100
                }
            ]
        };
        Assert.Null(TtsuProgressNormalizer.ResolveAuthoritativeTotal(bookConflicting));

        // Missing progress (no progress entries, only daily entries)
        var bookNoProgress = new TtsuBookContainer
        {
            Title = "No Progress Book",
            Entries =
            [
                new TtsuReaderDTO { DateKey = "2026-08-05", CharactersRead = 10000 }
            ]
        };
        Assert.Null(TtsuProgressNormalizer.ResolveAuthoritativeTotal(bookNoProgress));
    }

    [Fact]
    public async Task Preview_Enrichment_PerformsNoDatabaseWrites()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var candidate = CreateCandidate(101, title: "Test Book Candidate");
                var scored = CreateScored(candidate, 100);
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, scored)]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.True(model.HasPreview);
        Assert.NotNull(model.Books.Single().Enrichment);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
        Assert.Empty(await database.Context.ImmersionLogs.ToListAsync());
        Assert.Empty(await database.Context.TtsuBindings.ToListAsync());
    }

    [Fact]
    public async Task Preview_OutcomesAndCandidateKeys_RemainStableAcrossRefreshAndValidationRedisplay()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Candidate 1");
                var c2 = CreateCandidate(102, title: "Candidate 2");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100), CreateScored(c2, 80))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.Single(matchService.Invocations);
        var originalCandidates = model.Books.Single().Enrichment!.Candidates;
        Assert.Equal(2, originalCandidates.Count);
        var key1 = originalCandidates[0].Key;
        var key2 = originalCandidates[1].Key;

        // Refresh review (OnPostReviewAsync)
        await model.OnPostReviewAsync(CancellationToken.None);

        // MatchBatchAsync must NOT have been called again
        Assert.Single(matchService.Invocations);
        var refreshedCandidates = model.Books.Single().Enrichment!.Candidates;
        Assert.Equal(key1, refreshedCandidates[0].Key);
        Assert.Equal(key2, refreshedCandidates[1].Key);

        // Validation redisplay on invalid confirm
        model.Selections[0].Mode = (TtsuImportMode)999;
        await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.Single(matchService.Invocations);
        var redisplayedCandidates = model.Books.Single().Enrichment!.Candidates;
        Assert.Equal(key1, redisplayedCandidates[0].Key);
        Assert.Equal(key2, redisplayedCandidates[1].Key);
    }

    [Fact]
    public async Task Preview_CandidateKeys_AreNonEmptyAndUniqueWithinBatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var outcomes = new List<JitenMatchOutcome>();
                foreach (var req in reqs)
                {
                    var c1 = CreateCandidate(101, title: "C1");
                    var c2 = CreateCandidate(102, title: "C2");
                    outcomes.Add(CreateMatchedOutcome(req.CorrelationId, MatchConfidence.High, CreateScored(c1, 100), CreateScored(c2, 80)));
                }
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(outcomes);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(firstStream, "Test Book"), StatisticsFile(secondStream, "Second Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var allKeys = model.Books.SelectMany(b => b.Enrichment!.Candidates.Select(c => c.Key)).ToList();
        Assert.Equal(4, allKeys.Count);
        Assert.All(allKeys, key => Assert.NotEqual(Guid.Empty, key));
        Assert.Equal(allKeys.Distinct().Count(), allKeys.Count);
    }

    [Fact]
    public async Task Preview_DisqualifiedCandidate_IsDiagnosticOnlyAndCannotBeReviewedAsAChoice()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (requests, _, _) =>
            {
                var selectable = CreateScored(CreateCandidate(101, title: "Selectable"), 75) with
                {
                    Evidence = ["Exact title and volume matched"]
                };
                var disqualified = CreateScored(CreateCandidate(102, title: "Conflicting"), 0) with
                {
                    IsDisqualified = true,
                    DisqualificationReason = "Explicit volume conflict",
                    Evidence = ["Explicit volume conflict"]
                };
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(requests[0].CorrelationId, MatchConfidence.Review, selectable, disqualified)]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];

        await model.OnPostPreviewAsync(CancellationToken.None);

        var candidates = model.Books.Single().Enrichment!.Candidates;
        var selectableCandidate = candidates.Single(candidate => candidate.DeckId == 101);
        var disqualifiedCandidate = candidates.Single(candidate => candidate.DeckId == 102);
        Assert.True(selectableCandidate.IsSelectable);
        Assert.Equal(["Exact title and volume matched"], selectableCandidate.Evidence);
        Assert.False(disqualifiedCandidate.IsSelectable);
        Assert.Equal(["Explicit volume conflict"], disqualifiedCandidate.Evidence);

        model.Selections[0].CandidateKey = disqualifiedCandidate.Key;
        var result = await model.OnPostReviewAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Null(model.Selections[0].CandidateKey);
    }

    [Fact]
    public async Task Preview_HighConfidence_PreselectsTopCandidateWhenEligible()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Top Match");
                var c2 = CreateCandidate(102, title: "Second Match");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100), CreateScored(c2, 80))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        Assert.Equal(TtsuEnrichmentBadge.AutoMatched, preview.Enrichment!.Badge);
        var topKey = preview.Enrichment.Candidates[0].Key;
        Assert.Equal(topKey, model.Selections[0].CandidateKey);
        Assert.Equal(topKey, preview.Enrichment.SelectedCandidateKey);
        Assert.True(preview.Enrichment.IsMetadataApplicationEligible);
    }

    [Theory]
    [InlineData(MatchConfidence.Review)]
    [InlineData(MatchConfidence.None)]
    public async Task Preview_NonHighConfidence_DefaultsToNoMetadata(MatchConfidence confidence)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Candidate 1");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, confidence, CreateScored(c1, 50))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        Assert.Equal(confidence == MatchConfidence.Review ? TtsuEnrichmentBadge.NeedsReview : TtsuEnrichmentBadge.NoSafeMatch, preview.Enrichment!.Badge);
        Assert.Null(model.Selections[0].CandidateKey);
        Assert.Null(preview.Enrichment.SelectedCandidateKey);
    }

    [Theory]
    [InlineData(JitenMatchStatus.NoCandidates)]
    [InlineData(JitenMatchStatus.Unavailable)]
    [InlineData(JitenMatchStatus.RateLimited)]
    public async Task Preview_FailuresAndNoCandidates_DefaultToNoMetadata(JitenMatchStatus status)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var outcome = status switch
                {
                    JitenMatchStatus.NoCandidates => JitenMatchOutcome.NoCandidates(reqs[0].CorrelationId),
                    JitenMatchStatus.Unavailable => JitenMatchOutcome.Unavailable(reqs[0].CorrelationId, ["Jiten is down"]),
                    JitenMatchStatus.RateLimited => JitenMatchOutcome.RateLimited(reqs[0].CorrelationId, ["Rate limited"]),
                    _ => throw new InvalidOperationException()
                };
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([outcome]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var preview = model.Books.Single();
        Assert.Null(model.Selections[0].CandidateKey);
        Assert.Null(preview.Enrichment!.SelectedCandidateKey);
    }

    [Fact]
    public async Task Preview_UnavailableOutcome_LeavesReadingImportSelectedAndShowsWarning()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                [JitenMatchOutcome.Unavailable(reqs[0].CorrelationId, ["Jiten connection timed out"])])
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.True(model.Selections.Single().Selected);
        var preview = model.Books.Single();
        Assert.Equal(TtsuEnrichmentBadge.JitenUnavailable, preview.Enrichment!.Badge);
        Assert.Contains("Jiten connection timed out", preview.Enrichment.Warnings);
    }

    [Fact]
    public async Task Preview_TargetProtection_ExistingBindingLinkAndCoverEachDisableApplication()
    {
        await using var database = await TestDatabase.CreateAsync();
        // Target 1 has binding
        var workWithBinding = new Kiseki.Core.Entities.MediaWork("Book with binding");
        database.Context.MediaWorks.Add(workWithBinding);
        database.Context.TtsuBindings.Add(new() { MediaWorkId = workWithBinding.Id, OriginalTitle = "Book with binding" });

        // Target 2 has Jiten link
        var workWithLink = new Kiseki.Core.Entities.MediaWork("Book with link", jitenDeckId: 999);
        database.Context.MediaWorks.Add(workWithLink);

        // Target 3 has cover
        var workWithCover = new Kiseki.Core.Entities.MediaWork("Book with cover");
        typeof(Kiseki.Core.Entities.MediaWork).GetProperty("JitenCoverUrl")!.SetValue(workWithCover, "https://example.com/cover.jpg");
        database.Context.MediaWorks.Add(workWithCover);

        await database.Context.SaveChangesAsync();

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Top Match");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100))]);
            }
        };

        // Test with Target 1 (binding)
        using var s1 = File.OpenRead(GetFixturePath());
        var m1 = CreateModel(database.Context, matchService);
        m1.AutoMatchMetadata = true;
        m1.FolderFiles = [StatisticsFile(s1)];
        await m1.OnPostPreviewAsync(CancellationToken.None);
        m1.Selections[0].Mode = TtsuImportMode.Merge;
        m1.Selections[0].TargetId = workWithBinding.Id;
        await m1.OnPostReviewAsync(CancellationToken.None);
        Assert.False(m1.Books[0].Enrichment!.IsMetadataApplicationEligible);
        Assert.Contains("binding", m1.Books[0].Enrichment!.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(m1.Selections[0].CandidateKey);

        // Test with Target 2 (Jiten link)
        using var s2 = File.OpenRead(GetFixturePath());
        var m2 = CreateModel(database.Context, matchService);
        m2.AutoMatchMetadata = true;
        m2.FolderFiles = [StatisticsFile(s2)];
        await m2.OnPostPreviewAsync(CancellationToken.None);
        m2.Selections[0].Mode = TtsuImportMode.Merge;
        m2.Selections[0].TargetId = workWithLink.Id;
        await m2.OnPostReviewAsync(CancellationToken.None);
        Assert.False(m2.Books[0].Enrichment!.IsMetadataApplicationEligible);
        Assert.Contains("Jiten", m2.Books[0].Enrichment!.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(m2.Selections[0].CandidateKey);

        // Test with Target 3 (Cover)
        using var s3 = File.OpenRead(GetFixturePath());
        var m3 = CreateModel(database.Context, matchService);
        m3.AutoMatchMetadata = true;
        m3.FolderFiles = [StatisticsFile(s3)];
        await m3.OnPostPreviewAsync(CancellationToken.None);
        m3.Selections[0].Mode = TtsuImportMode.Merge;
        m3.Selections[0].TargetId = workWithCover.Id;
        await m3.OnPostReviewAsync(CancellationToken.None);
        Assert.False(m3.Books[0].Enrichment!.IsMetadataApplicationEligible);
        Assert.Contains("cover", m3.Books[0].Enrichment!.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(m3.Selections[0].CandidateKey);
    }

    [Fact]
    public async Task Preview_TargetProtection_ChangingTargetRecomputesEligibility()
    {
        await using var database = await TestDatabase.CreateAsync();
        var protectedWork = new Kiseki.Core.Entities.MediaWork("Protected", jitenDeckId: 50);
        database.Context.MediaWorks.Add(protectedWork);
        await database.Context.SaveChangesAsync();

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Top Match");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100))]);
            }
        };

        using var fixtureStream = File.OpenRead(GetFixturePath());
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        // Initial preview: new work (Create) -> eligible
        Assert.True(model.Books[0].Enrichment!.IsMetadataApplicationEligible);
        Assert.NotNull(model.Selections[0].CandidateKey);

        // Change target to protectedWork
        model.Selections[0].Mode = TtsuImportMode.Merge;
        model.Selections[0].TargetId = protectedWork.Id;
        await model.OnPostReviewAsync(CancellationToken.None);

        // Recomputed -> ineligible, candidateKey cleared
        Assert.False(model.Books[0].Enrichment!.IsMetadataApplicationEligible);
        Assert.Null(model.Selections[0].CandidateKey);

        // Change mode back to Create
        model.Selections[0].Mode = TtsuImportMode.Create;
        model.Selections[0].TargetId = null;
        await model.OnPostReviewAsync(CancellationToken.None);

        // Recomputed -> eligible again
        Assert.True(model.Books[0].Enrichment!.IsMetadataApplicationEligible);
    }

    [Fact]
    public async Task Confirm_UnknownAndCrossBookCandidateKeys_AreRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var outcomes = new List<JitenMatchOutcome>();
                foreach (var req in reqs)
                {
                    var c = CreateCandidate(101, title: req.RawTitle);
                    outcomes.Add(CreateMatchedOutcome(req.CorrelationId, MatchConfidence.High, CreateScored(c, 100)));
                }
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(outcomes);
            }
        };

        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(firstStream, "Test Book"), StatisticsFile(secondStream, "Second Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        // Unknown candidate key
        model.Selections[0].CandidateKey = Guid.NewGuid();
        var unknownResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<PageResult>(unknownResult);
        Assert.False(model.ModelState.IsValid);

        // Cross-book candidate key: Book 0 gets Book 1's candidate key
        model.ModelState.Clear();
        await model.OnPostReviewAsync(CancellationToken.None); // refresh to valid state
        var book1CandidateKey = model.Books[1].Enrichment!.Candidates[0].Key;
        model.Selections[0].CandidateKey = book1CandidateKey;
        var crossResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<PageResult>(crossResult);
        Assert.False(model.ModelState.IsValid);
    }

    [Fact]
    public async Task Confirm_CandidateChangedWithOldReviewToken_RequiresRefresh()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Candidate 1");
                var c2 = CreateCandidate(102, title: "Candidate 2");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100), CreateScored(c2, 80))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var candidate2Key = model.Books[0].Enrichment!.Candidates[1].Key;
        // User changes candidate choice in form without refreshing review
        model.Selections[0].CandidateKey = candidate2Key;

        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
    }

    [Fact]
    public async Task Review_RefreshedCandidateChoice_ReceivesValidNewReviewToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Candidate 1");
                var c2 = CreateCandidate(102, title: "Candidate 2");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100), CreateScored(c2, 80))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var oldToken = model.Selections[0].ReviewToken;
        var candidate2Key = model.Books[0].Enrichment!.Candidates[1].Key;
        model.Selections[0].CandidateKey = candidate2Key;

        // Call Refresh review
        await model.OnPostReviewAsync(CancellationToken.None);

        var newToken = model.Selections[0].ReviewToken;
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(candidate2Key, model.Selections[0].CandidateKey);

        // Now confirm succeeds!
        var confirmResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(confirmResult);
    }

    [Fact]
    public async Task Confirm_ReviewedCandidate_WritesOnlyReadingHistoryAndNoJitenMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var c1 = CreateCandidate(101, title: "Enriched Deck", characters: 50_000, coverUrl: "https://example.com/c.jpg");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                    [CreateMatchedOutcome(reqs[0].CorrelationId, MatchConfidence.High, CreateScored(c1, 100))]);
            }
        };
        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        // Confirm
        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.IsType<RedirectToPageResult>(result);

        // Inspect database: reading history imported, but ZERO Jiten metadata written
        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.Null(work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Null(work.JitenCoverUrl);
        Assert.Null(work.JitenCharacterCount);
        Assert.Equal(2, work.Logs.Count);
    }

    [Fact]
    public async Task Preview_PartialFailure_UnavailableForOneBookDoesNotEraseSuggestionForAnother()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var req1 = reqs.Single(r => r.RawTitle == "Test Book");
                var req2 = reqs.Single(r => r.RawTitle == "Second Book");
                var c2 = CreateCandidate(202, title: "Second Book Match");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                    JitenMatchOutcome.Unavailable(req1.CorrelationId, ["Jiten unavailable for book 1"]),
                    CreateMatchedOutcome(req2.CorrelationId, MatchConfidence.High, CreateScored(c2, 100))
                ]);
            }
        };

        var model = CreateModel(database.Context, matchService);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(firstStream, "Test Book"), StatisticsFile(secondStream, "Second Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var book1 = model.Books.Single(b => b.Title == "Test Book");
        var book2 = model.Books.Single(b => b.Title == "Second Book");

        Assert.Equal(TtsuEnrichmentBadge.JitenUnavailable, book1.Enrichment!.Badge);
        Assert.Contains("Jiten unavailable for book 1", book1.Enrichment.Warnings);

        Assert.Equal(TtsuEnrichmentBadge.AutoMatched, book2.Enrichment!.Badge);
        Assert.Single(book2.Enrichment.Candidates);
    }

    [Fact]
    public void AutoMatchMetadata_DefaultsToFalseUntilManualCalibrationGateIsComplete()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var model = new TtsuModel(
            new TtsuDataLoader(),
            new TtsuImportBatchStore(memory),
            null!,
            new StubJitenMatchService(),
            new StubJitenSelectionResolver());

        Assert.False(model.AutoMatchMetadata);
    }

    [Fact]
    public async Task Confirm_NoSelectedCandidate_MakesNoResolverCall_ImportsReadingNormally()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());
        var resolver = new StubJitenSelectionResolver();
        var model = CreateModel(database.Context, selectionResolver: resolver);
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);

        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.Empty(resolver.Invocations);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.False(work.HasJitenLink);
        Assert.Equal(2, work.Logs.Count);
    }

    [Fact]
    public async Task Confirm_ResolvesUsingServerOwnedDeckAndSubdeckIds()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, subdeckId: 15, title: "Series Match");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Fresh Original", "Fresh Romaji", "Fresh English", 75_000, "https://cdn.jiten.moe/fresh.jpg", 0, JitenCoverEvidence.Specific)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        Assert.NotNull(model.Selections[0].CandidateKey);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);

        var invocation = Assert.Single(resolver.Invocations);
        Assert.Equal(10, invocation.DeckId);
        Assert.Equal(15, invocation.SubdeckId);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(15, work.JitenSubdeckId);
    }

    [Fact]
    public async Task Confirm_FreshResolverValuesPersisted_NotStalePreviewValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, subdeckId: null, title: "Stale Preview Title", characters: 50_000, coverUrl: "https://cdn.jiten.moe/stale.jpg");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Fresh Original Title", "Fresh Romaji", "Fresh English", 99_999, "https://cdn.jiten.moe/fresh.jpg", 0, JitenCoverEvidence.Specific)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(99_999, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/fresh.jpg", work.JitenCoverUrl);
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal("Test Book", work.Title);
    }

    [Fact]
    public async Task Confirm_StandaloneDeckSelection_AppliedToNewWork()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(42, subdeckId: null, title: "Standalone Match");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(42, null, "Standalone Jiten", "Romaji", "English", 80_000, "https://cdn.jiten.moe/standalone.jpg", 0, JitenCoverEvidence.Specific)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(42, work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Equal(80_000, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/standalone.jpg", work.JitenCoverUrl);
        Assert.Equal("Test Book", work.Title);
    }

    [Fact]
    public async Task Confirm_SubdeckSelection_PersistsParentDeckSubdeckCountAndCover()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, subdeckId: 20, title: "Subdeck Match");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Subdeck Novel", "Romaji", "English", 62_000, "https://cdn.jiten.moe/volume1.jpg", 0, JitenCoverEvidence.Specific)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(20, work.JitenSubdeckId);
        Assert.Equal(62_000, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/volume1.jpg", work.JitenCoverUrl);
        Assert.Equal("Test Book", work.Title);
    }

    [Fact]
    public async Task Confirm_AutomaticLinking_RetainsTtsuTitle()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, subdeckId: null, title: "Candidate Title");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(10, null, "異世界ノベル", "Isekai Novel", "Another World Novel", 50_000, null, 0, JitenCoverEvidence.None)))
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        await model.OnPostConfirmAsync(CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.SingleAsync();
        Assert.Equal("Test Book", work.Title);
    }

    [Theory]
    [InlineData("parent_children")]
    [InlineData("subdeck_not_found")]
    [InlineData("mismatched_parent")]
    [InlineData("invalid_char_count")]
    [InlineData("http_429")]
    [InlineData("http_500")]
    [InlineData("transport_error")]
    [InlineData("malformed_json")]
    [InlineData("timeout")]
    [InlineData("timeout_with_request_token")]
    public async Task Confirm_ResolverFailures_BecomeSkipsWhileReadingImports(string failureMode)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, title: "Failed Candidate");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (_, _, ct) => failureMode switch
            {
                "parent_children" => Task.FromResult(JitenSelectionResult.Failed(JitenSelectionStatus.ParentHasChildren, "Parent has children")),
                "subdeck_not_found" => Task.FromResult(JitenSelectionResult.Failed(JitenSelectionStatus.SubdeckNotFound, "Subdeck not found")),
                "mismatched_parent" => Task.FromResult(JitenSelectionResult.Failed(JitenSelectionStatus.MismatchedParent, "Mismatched parent")),
                "invalid_char_count" => Task.FromResult(JitenSelectionResult.Failed(JitenSelectionStatus.InvalidCharacterCount, "Invalid character count")),
                "http_429" => throw new JitenHttpException("Rate limited", System.Net.HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)),
                "http_500" => throw new JitenHttpException("Server error", System.Net.HttpStatusCode.InternalServerError, null),
                "transport_error" => throw new HttpRequestException("Network failure"),
                "malformed_json" => throw new System.Text.Json.JsonException("Malformed JSON"),
                "timeout" => throw new TaskCanceledException("HttpClient timeout", new TimeoutException()),
                "timeout_with_request_token" => throw new OperationCanceledException("HttpClient timeout", ct),
                _ => throw new ArgumentOutOfRangeException(nameof(failureMode))
            }
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);

        database.Context.ChangeTracker.Clear();
        var work = await database.Context.MediaWorks.Include(w => w.Logs).SingleAsync();
        Assert.Equal("Test Book", work.Title);
        Assert.False(work.HasJitenLink);
        Assert.Null(work.JitenCoverUrl);
        Assert.Equal(2, work.Logs.Count);

        var notice = (string)model.TempData["LibraryNotice"]!;
        Assert.Contains("0 metadata linked, 1 metadata skipped", notice);
    }

    [Fact]
    public async Task Confirm_UnexpectedResolverFailure_PropagatesWithoutCommitting()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, title: "Unexpected Failure Candidate");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };
        var resolver = new StubJitenSelectionResolver
        {
            Handler = (_, _, _) => throw new InvalidOperationException("Programming failure")
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.OnPostConfirmAsync(CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
        Assert.Empty(await database.Context.ImmersionLogs.ToListAsync());
        Assert.Empty(await database.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task Confirm_CallerCancellation_PropagatesWithoutCommitting()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, title: "Cancelled Candidate");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        using var cts = new CancellationTokenSource();
        var resolver = new StubJitenSelectionResolver
        {
            Handler = (_, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(JitenSelectionResult.Succeeded(
                    new JitenMediaSelection(10, null, "Title", "Romaji", "English", 50_000, null, 0, JitenCoverEvidence.None)));
            }
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => model.OnPostConfirmAsync(cts.Token));

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.MediaWorks.ToListAsync());
        Assert.Empty(await database.Context.ImmersionLogs.ToListAsync());
        Assert.Empty(await database.Context.TtsuImportReceipts.ToListAsync());
    }

    [Fact]
    public async Task Confirm_VerifiesWholeBatchBeforeApply_AndOneFailureDoesNotDiscardOtherMetadataOrReading()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await File.ReadAllTextAsync(GetFixturePath());
        using var firstStream = new MemoryStream(Encoding.UTF8.GetBytes(fixture));
        using var secondStream = new MemoryStream(Encoding.UTF8.GetBytes(
            fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal)));

        var candidate1 = CreateCandidate(10, title: "Book 1 Match");
        var candidate2 = CreateCandidate(20, title: "Book 2 Match");

        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) =>
            {
                var req1 = reqs.Single(r => r.RawTitle == "Test Book");
                var req2 = reqs.Single(r => r.RawTitle == "Second Book");
                return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                    CreateMatchedOutcome(req1.CorrelationId, MatchConfidence.High, CreateScored(candidate1, 100)),
                    CreateMatchedOutcome(req2.CorrelationId, MatchConfidence.High, CreateScored(candidate2, 100))
                ]);
            }
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) =>
            {
                Assert.False(database.Context.MediaWorks.Any());
                Assert.False(database.Context.ImmersionLogs.Any());
                Assert.False(database.Context.TtsuImportReceipts.Any());
                if (deckId == 10)
                {
                    return Task.FromResult(JitenSelectionResult.Failed(JitenSelectionStatus.DeckNotFound, "Not found"));
                }
                return Task.FromResult(JitenSelectionResult.Succeeded(
                    new JitenMediaSelection(20, null, "Second Book Jiten", "Romaji", "English", 60_000, "https://cdn.jiten.moe/2.jpg", 0, JitenCoverEvidence.Specific)));
            }
        };

        var model = CreateModel(database.Context, matchService, resolver);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(firstStream, "Test Book"), StatisticsFile(secondStream, "Second Book")];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var result = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(result).PageName);

        database.Context.ChangeTracker.Clear();
        var works = await database.Context.MediaWorks.Include(w => w.Logs).OrderBy(w => w.Title).ToListAsync();
        Assert.Equal(2, works.Count);

        var secondWork = works.Single(w => w.Title == "Second Book");
        Assert.Equal(20, secondWork.JitenDeckId);
        Assert.Equal("https://cdn.jiten.moe/2.jpg", secondWork.JitenCoverUrl);
        Assert.Equal(2, secondWork.Logs.Count);

        var firstWork = works.Single(w => w.Title == "Test Book");
        Assert.False(firstWork.HasJitenLink);
        Assert.Null(firstWork.JitenCoverUrl);
        Assert.Equal(2, firstWork.Logs.Count);

        var notice = (string)model.TempData["LibraryNotice"]!;
        Assert.Contains("1 metadata linked, 1 metadata skipped", notice);
    }

    [Fact]
    public async Task Confirm_ReplayedOperationId_ReturnsReceiptWithoutCallingResolver()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var fixtureStream = File.OpenRead(GetFixturePath());

        var candidate = CreateCandidate(10, title: "Replay Match");
        var matchService = new StubJitenMatchService
        {
            Handler = (reqs, _, _) => Task.FromResult<IReadOnlyList<JitenMatchOutcome>>([
                CreateMatchedOutcome(reqs.Single().CorrelationId, MatchConfidence.High, CreateScored(candidate, 100))
            ])
        };

        var resolver = new StubJitenSelectionResolver
        {
            Handler = (deckId, subdeckId, _) => Task.FromResult(JitenSelectionResult.Succeeded(
                new JitenMediaSelection(deckId, subdeckId, "Title", "Romaji", "English", 50_000, "https://cdn.jiten.moe/c.jpg", 0, JitenCoverEvidence.Specific)))
        };

        var batchStore = new TtsuImportBatchStore(new MemoryCache(new MemoryCacheOptions()));
        var model = CreateModel(database.Context, matchService, resolver, batchStore);
        model.AutoMatchMetadata = true;
        model.FolderFiles = [StatisticsFile(fixtureStream)];
        await model.OnPostPreviewAsync(CancellationToken.None);

        var batchId = model.BatchId;
        var firstResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(firstResult).PageName);
        var callsAfterFirst = resolver.Invocations.Count;
        Assert.Equal(1, callsAfterFirst);

        Assert.False(batchStore.TryGet(batchId, out _));

        model.BatchId = batchId;
        var replayedResult = await model.OnPostConfirmAsync(CancellationToken.None);
        Assert.Equal("/Library/Index", Assert.IsType<RedirectToPageResult>(replayedResult).PageName);

        Assert.Equal(callsAfterFirst, resolver.Invocations.Count);
        var notice = (string)model.TempData["LibraryNotice"]!;
        Assert.Contains("1 metadata linked, 0 metadata skipped", notice);
    }

    private static JitenMatchCandidate CreateCandidate(
        int deckId,
        int? subdeckId = null,
        string title = "Test Match",
        int characters = 50_000,
        string? coverUrl = "https://example.com/cover.jpg",
        JitenCoverEvidence coverEvidence = JitenCoverEvidence.Specific) =>
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
        ITtsuImportBatchStore? batchStore = null)
    {
        var httpContext = new DefaultHttpContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var model = new TtsuModel(
            new TtsuDataLoader(),
            batchStore ?? new TtsuImportBatchStore(cache),
            context,
            matchService ?? new StubJitenMatchService(),
            selectionResolver ?? new StubJitenSelectionResolver())
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };

        return model;
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

    private static FormFile ProgressFile(Stream stream, string folderTitle = "Test Book")
    {
        return new FormFile(
            stream,
            0,
            stream.Length,
            "FolderFiles",
            $"ttu-reader-data/{folderTitle}/progress_1_6_1234_0.25.json");
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

    public sealed class StubJitenMatchService : IJitenMatchService
    {
        public Func<IReadOnlyList<JitenMatchRequest>, TimeSpan, CancellationToken, Task<IReadOnlyList<JitenMatchOutcome>>>? Handler { get; set; }
        public List<IReadOnlyList<JitenMatchRequest>> Invocations { get; } = [];

        public Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
            IReadOnlyList<JitenMatchRequest> requests,
            TimeSpan timeBudget,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(requests);
            if (Handler is not null)
            {
                return Handler(requests, timeBudget, cancellationToken);
            }

            return Task.FromResult<IReadOnlyList<JitenMatchOutcome>>(
                requests.Select(r => JitenMatchOutcome.NoCandidates(r.CorrelationId)).ToList());
        }
    }

    public sealed class StubJitenSelectionResolver : IJitenSelectionResolver
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
}
