using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Covers;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;
using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Web.Models;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kiseki.Web.Pages.Import;

[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueCountLimit = 100_000)]
public sealed class TtsuModel(
    TtsuDataLoader dataLoader,
    ITtsuImportBatchStore batchStore,
    ImmersionDbContext dbContext,
    IJitenMatchService matchService,
    IJitenSelectionResolver selectionResolver,
    IGoogleBooksCoverService? googleBooksCoverService = null,
    ILogger<TtsuModel>? logger = null) : PageModel
{
    private const long MaxRequestBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan PerBookJitenBudget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PerBookCoverBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CoverEnrichmentTimeBudget = TimeSpan.FromSeconds(20);
    private readonly TtsuImportService _imports = new(dbContext);

    [BindProperty] public List<IFormFile> FolderFiles { get; set; } = [];
    [BindProperty] public Guid BatchId { get; set; }
    [BindProperty] public bool AutoMatchMetadata { get; set; }
    [BindProperty] public List<TtsuBookSelectionInput> Selections { get; set; } = [];
    public IReadOnlyList<TtsuBookPreviewViewModel> Books { get; private set; } = [];
    public IReadOnlyList<TtsuTarget> Targets { get; private set; } = [];
    public IReadOnlyList<TtsuOrphanViewModel> Orphans { get; private set; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasPreview => BatchId != Guid.Empty && Books.Count > 0;
    public bool IsEnrichmentActive { get; private set; }
    public int EnrichmentCompletedCount { get; private set; }
    public int EnrichmentTotalCount { get; private set; }
    public bool IsGoogleBooksConfigured => googleBooksCoverService?.IsConfigured ?? false;
    public void OnGet() { }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        var files = FolderFiles.Where(file => TtsuDataLoader.IsStatisticsFileName(file.FileName) ||
            TtsuDataLoader.IsProgressFileName(file.FileName)).ToList();
        var statisticsFileCount = files.Count(file => TtsuDataLoader.IsStatisticsFileName(file.FileName));
        if (statisticsFileCount == 0 || files.Count > 250)
        {
            ModelState.AddModelError(nameof(FolderFiles), "Choose a TTSU folder with statistics files and no more than 250 statistics/progress files.");
            return Page();
        }
        var parsed = new List<TtsuBookContainer>();
        var progressByFolder = new Dictionary<string, List<TtsuProgressDTO>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.FileName.Replace('\\', '/');
            if (file.Length is 0 or > 8 * 1024 * 1024)
            {
                Warnings.Add($"{path} was empty or larger than 8 MB and was skipped.");
                continue;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var folder = parts.Length > 1 ? parts[^2] : null;
                // Ignore the chosen root directory, which changes between exports/machines.
                var folderHint = parts.Length > 2 ? string.Join('/', parts.Skip(1).SkipLast(1)) : folder;
                if (TtsuDataLoader.IsProgressFileName(path))
                {
                    var progress = await dataLoader.ParseProgressAsync(stream, path, cancellationToken);
                    progressByFolder.TryAdd(folderHint ?? string.Empty, []);
                    progressByFolder[folderHint ?? string.Empty].Add(progress);
                }
                else
                {
                    var book = await dataLoader.ParseStatisticsAsync(stream, folder, cancellationToken);
                    book.FolderHint = folderHint;
                    parsed.Add(book);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                Warnings.Add($"{path} was skipped: {exception.Message}");
            }
        }
        if (parsed.Count == 0)
        {
            ModelState.AddModelError(nameof(FolderFiles), "None of the detected statistics files could be read.");
            return Page();
        }
        var combined = TtsuStatisticsNormalizer.CombineFiles(parsed).ToList();
        foreach (var (folderHint, progressEntries) in progressByFolder)
        {
            var matches = combined.Where(book => string.Equals(book.FolderHint ?? string.Empty,
                folderHint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                matches[0].ProgressEntries.AddRange(progressEntries);
            }
            else
            {
                Warnings.Add(matches.Count == 0
                    ? $"Progress in '{folderHint}' was skipped because no statistics file identified its book."
                    : $"Progress in '{folderHint}' was skipped because multiple book titles made it ambiguous.");
            }
        }
        var batch = batchStore.Store(combined);

        if (AutoMatchMetadata)
        {
            foreach (var item in batch.Books)
            {
                item.EnrichmentState = TtsuEnrichmentAttemptState.Pending;
            }
        }

        await PopulateAsync(batch, false, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostEnrichNextAsync(
        [FromQuery] Guid? batchId,
        CancellationToken cancellationToken)
    {
        var effectiveBatchId = batchId ?? BatchId;
        if (effectiveBatchId == Guid.Empty && Request.HasFormContentType && Request.Form.TryGetValue("batchId", out var formBatchId))
        {
            _ = Guid.TryParse(formBatchId, out effectiveBatchId);
        }

        if (effectiveBatchId == Guid.Empty || !batchStore.TryGet(effectiveBatchId, out var batch))
        {
            return NotFound(new { error = "Import preview not found or expired.", complete = true });
        }

        string ResolveGoogleSecondaryText()
        {
            if (!IsGoogleBooksConfigured)
            {
                return "Google Books is not configured";
            }

            int checkedBooksCount;
            int coversFoundCount;
            lock (batch.SyncLock)
            {
                checkedBooksCount = batch.Books.Count(b => b.Enrichment?.Candidates.Any(c => c.GoogleCoverStatus is not null) == true);
                coversFoundCount = batch.Books.Count(b => b.Enrichment?.Candidates.Any(c => c.GoogleCover is not null) == true);
            }

            if (checkedBooksCount > 0)
            {
                var booksLabel = checkedBooksCount == 1 ? "1 book" : $"{checkedBooksCount} books";
                var coversLabel = coversFoundCount == 1 ? "1 volume cover found" : $"{coversFoundCount} volume covers found";
                return $"Google Books checked for {booksLabel} · {coversLabel}";
            }

            return "Google Books ready";
        }

        if (!batch.TryClaimNextPendingBook(out var claimedBook, out var completedCount, out var totalCount, out var currentNumber))
        {
            return new JsonResult(new
            {
                processed = completedCount,
                total = totalCount,
                complete = completedCount == totalCount,
                currentNumber = currentNumber,
                currentTitle = (string?)null,
                message = $"Checked {completedCount} of {totalCount} books",
                googleSecondaryText = ResolveGoogleSecondaryText()
            });
        }

        if (claimedBook is null)
        {
            return new JsonResult(new
            {
                processed = completedCount,
                total = totalCount,
                complete = true,
                currentNumber = totalCount,
                currentTitle = (string?)null,
                message = $"Checked {totalCount} of {totalCount} books",
                googleSecondaryText = ResolveGoogleSecondaryText()
            });
        }

        try
        {
            var matchReq = new JitenMatchRequest
            {
                CorrelationId = claimedBook.Key,
                RawTitle = claimedBook.Book.Title,
                AuthoritativeTtsuTotal = TtsuProgressNormalizer.ResolveAuthoritativeTotal(claimedBook.Book)
            };

            var outcomes = await matchService.MatchBatchAsync([matchReq], PerBookJitenBudget, cancellationToken);
            var outcome = outcomes.FirstOrDefault() ?? JitenMatchOutcome.Unavailable(
                claimedBook.Key,
                warnings: ["Operation timed out before match could complete."]);

            var enrichment = CreateEnrichment(outcome);
            claimedBook.Enrichment = enrichment;

            if (googleBooksCoverService is not null && IsGoogleBooksConfigured)
            {
                using var coverCts = new CancellationTokenSource(PerBookCoverBudget);
                using var linkedCoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, coverCts.Token);

                if (await CanResolveGoogleCoverAsync(claimedBook, linkedCoverCts.Token))
                {
                    if (enrichment.Confidence == MatchConfidence.High)
                    {
                        var topCandidate = enrichment.Candidates.FirstOrDefault(c => c.IsTopCandidate && c.IsSelectable);
                        if (topCandidate is not null)
                        {
                            await ResolveGoogleCoverForCandidateAsync(claimedBook, topCandidate, linkedCoverCts.Token, cancellationToken);
                        }
                    }
                    else if (enrichment.Confidence is MatchConfidence.Review or MatchConfidence.None)
                    {
                        var exactCandidates = enrichment.Candidates
                            .Where(c => c.IsExactIdentity && c.IsSelectable)
                            .Take(3)
                            .ToList();

                        foreach (var exactCandidate in exactCandidates)
                        {
                            if (linkedCoverCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                ApplyGoogleCoverResult(
                                    claimedBook,
                                    exactCandidate,
                                    GoogleBooksCoverMatchResult.CreateTimedOut("Google Books lookup timed out."));
                                continue;
                            }

                            await ResolveGoogleCoverForCandidateAsync(claimedBook, exactCandidate, linkedCoverCts.Token, cancellationToken);
                        }
                    }
                }
            }

            lock (batch.SyncLock)
            {
                claimedBook.EnrichmentState = TtsuEnrichmentAttemptState.Completed;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (batch.SyncLock)
            {
                if (claimedBook.EnrichmentState == TtsuEnrichmentAttemptState.InProgress)
                {
                    claimedBook.EnrichmentState = TtsuEnrichmentAttemptState.Pending;
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            lock (batch.SyncLock)
            {
                if (claimedBook.EnrichmentState == TtsuEnrichmentAttemptState.InProgress)
                {
                    claimedBook.EnrichmentState = TtsuEnrichmentAttemptState.Pending;
                }
            }
            logger?.LogWarning(ex, "Unexpected error enriching book '{Title}'", claimedBook.Book.Title);
            throw;
        }

        int updatedCompleted;
        int total;
        bool isComplete;
        int nextNumber;
        string? nextTitle;

        lock (batch.SyncLock)
        {
            total = batch.Books.Count;
            updatedCompleted = batch.Books.Count(b => b.EnrichmentState == TtsuEnrichmentAttemptState.Completed);
            isComplete = updatedCompleted == total;
            var nextPending = batch.Books.FirstOrDefault(b => b.EnrichmentState == TtsuEnrichmentAttemptState.Pending);
            nextNumber = Math.Min(total, updatedCompleted + 1);
            nextTitle = nextPending?.Book.Title ?? claimedBook.Book.Title;
        }

        return new JsonResult(new
        {
            processed = updatedCompleted,
            total = total,
            complete = isComplete,
            currentNumber = nextNumber,
            currentTitle = nextTitle,
            message = $"Checked {updatedCompleted} of {total} books",
            googleSecondaryText = ResolveGoogleSecondaryText()
        });
    }

    public async Task<IActionResult> OnPostReviewAsync(
        CancellationToken cancellationToken = default,
        [FromQuery] bool? fromEnrichment = null)
    {
        if (!TryBatch(out var batch)) return Page();

        var isFromEnrichment = fromEnrichment == true;

        if (googleBooksCoverService is not null && IsGoogleBooksConfigured)
        {
            var posted = Selections.GroupBy(x => x.BookKey).ToDictionary(x => x.Key, x => x.First());
            var coverTargets = batch.Books
                .Select(item =>
                {
                    posted.TryGetValue(item.Key, out var input);
                    var selectedCandidate = input?.CandidateKey is Guid candidateKey
                        ? item.Enrichment?.Candidates.FirstOrDefault(c =>
                            c.Key == candidateKey && c.IsSelectable && c.GoogleCoverStatus is null)
                        : null;
                    return (Book: item, Candidate: selectedCandidate);
                })
                .Where(target => target.Candidate is not null)
                .Select(target => (target.Book, Candidate: target.Candidate!));

            await ResolveGoogleCoversWithinBudgetAsync(coverTargets, cancellationToken);
        }

        await PopulateAsync(batch, true, cancellationToken, autoSelectHighConfidence: isFromEnrichment);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        var receipt = await _imports.GetReceiptAsync(BatchId, cancellationToken);
        if (receipt is not null) return Success(receipt);
        if (!TryBatch(out var batch)) return Page();
        if (!ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "One or more import choices were invalid. Review the selections.");
            await PopulateAsync(batch, true, cancellationToken);
            return Page();
        }
        try
        {
            var selected = Selections.Where(x => x.Selected).ToList();
            if (selected.Select(x => x.BookKey).Distinct().Count() != selected.Count)
                throw new TtsuImportReviewRequiredException("A book was selected more than once.");

            var prepared = new List<(TtsuBookSelectionInput Input, TtsuImportBatchBook Book, TtsuReviewedPlan Review, TtsuEnrichmentCandidate? Candidate)>();
            foreach (var input in selected)
            {
                var book = batch.Books.SingleOrDefault(x => x.Key == input.BookKey);
                if (book is null || !Enum.IsDefined(input.Mode) ||
                    !batch.Reviews.TryGetValue(input.ReviewToken, out var review) ||
                    review.BookKey != input.BookKey ||
                    review.CandidateKey != input.CandidateKey ||
                    review.SelectedCoverKey != input.SelectedCoverKey)
                {
                    throw new TtsuImportReviewRequiredException("The selection is invalid. Review the import again.");
                }

                TtsuEnrichmentCandidate? candidate = null;
                if (input.CandidateKey.HasValue)
                {
                    candidate = book.Enrichment?.Candidates.SingleOrDefault(c => c.Key == input.CandidateKey.Value && c.IsSelectable);
                    if (candidate is null)
                    {
                        throw new TtsuImportReviewRequiredException("The metadata selection is invalid. Review the import again.");
                    }
                }

                if (input.Mode == TtsuImportMode.Merge && input.TargetId is null)
                    throw new TtsuImportReviewRequiredException("Choose an existing book or explicitly choose Create a new copy.");

                prepared.Add((input, book, review, candidate));
            }

            var requests = new List<TtsuImportRequest>();
            foreach (var (input, book, review, candidate) in prepared)
            {
                TtsuMetadataImportRequest? metadataRequest = null;
                if (candidate is not null)
                {
                    JitenMediaSelection? freshSelection = null;
                    try
                    {
                        var resolveResult = await selectionResolver.ResolveAsync(
                            candidate.Candidate.DeckId,
                            candidate.Candidate.SubdeckId,
                            cancellationToken);

                        if (resolveResult.IsSuccess && resolveResult.Selection is not null)
                        {
                            freshSelection = resolveResult.Selection;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        freshSelection = null;
                    }
                    catch (HttpRequestException)
                    {
                        freshSelection = null;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        freshSelection = null;
                    }
                    catch (TimeoutException)
                    {
                        freshSelection = null;
                    }

                    ExternalCoverSelection? freshCover = null;
                    if (candidate is not null && googleBooksCoverService is not null)
                    {
                        CoverEditionOption? chosenOption = null;
                        var isExplicitNoCover = string.Equals(review.SelectedCoverKey, "none", StringComparison.OrdinalIgnoreCase);

                        if (candidate.CoverOptions is { Count: > 0 } options)
                        {
                            if (!isExplicitNoCover)
                            {
                                if (!string.IsNullOrWhiteSpace(review.SelectedCoverKey))
                                {
                                    chosenOption = options.FirstOrDefault(o => o.SelectionKey == review.SelectedCoverKey);
                                    if (chosenOption is null)
                                    {
                                        throw new TtsuImportReviewRequiredException("The selected cover edition is no longer available. Review the import again.");
                                    }
                                }
                                else
                                {
                                    chosenOption = options[0];
                                }
                            }
                        }

                        if (!isExplicitNoCover && (chosenOption is not null || candidate.GoogleCover is not null))
                        {
                            var provider = chosenOption?.Provider ?? candidate.GoogleCover!.Provider;
                            var providerItemId = chosenOption?.ProviderItemId ?? candidate.GoogleCover!.VolumeId;

                            try
                            {
                                var parsedTtsu = MediaTitleParser.ParseTitle(book.Book.Title);
                                var subdeckVolume = MediaTitleParser.ExtractChildVolume(candidate.Candidate.OriginalTitle, candidate.Candidate.IsSubdeck);

                                var context = new GoogleCoverLookupContext(
                                    book.Book.Title,
                                    parsedTtsu,
                                    parsedTtsu.Volume,
                                    candidate.Candidate.ParentOriginalTitle,
                                    candidate.Candidate.ParentRomajiTitle,
                                    candidate.Candidate.ParentEnglishTitle,
                                    candidate.Candidate.OriginalTitle,
                                    subdeckVolume,
                                    candidate.Candidate.IsStandalone ? candidate.Candidate.OriginalTitle : null,
                                    candidate.Candidate.IsStandalone ? candidate.Candidate.RomajiTitle : null,
                                    candidate.Candidate.IsStandalone ? candidate.Candidate.EnglishTitle : null,
                                    candidate.Candidate.IsSubdeck,
                                    candidate.Candidate.IsStandalone,
                                    candidate.Candidate.DeckId,
                                    candidate.Candidate.SubdeckId,
                                    candidate.IsExactIdentity);

                                var verifyResult = await googleBooksCoverService.VerifyCoverAsync(
                                    provider,
                                    providerItemId,
                                    context,
                                    cancellationToken);

                                if (verifyResult.IsMatched)
                                {
                                    if (verifyResult.Provider != provider)
                                    {
                                        throw new TtsuImportReviewRequiredException("The cover provider identity changed during confirmation. Review the import again.");
                                    }

                                    freshCover = new ExternalCoverSelection(
                                        verifyResult.Provider,
                                        verifyResult.CoverUrl!,
                                        verifyResult.VolumeId!,
                                        verifyResult.AttributionLink);
                                }
                                else if (!string.IsNullOrWhiteSpace(review.SelectedCoverKey) && candidate.CoverOptions is { Count: > 0 })
                                {
                                    throw new TtsuImportReviewRequiredException($"The selected cover for '{book.Book.Title}' is no longer available ({verifyResult.Warning}). Review the import again.");
                                }
                                else
                                {
                                    freshCover = null;
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (TtsuImportReviewRequiredException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                if (!string.IsNullOrWhiteSpace(review.SelectedCoverKey) && candidate.CoverOptions is { Count: > 0 })
                                {
                                    throw new TtsuImportReviewRequiredException($"Verification failed for the selected cover of '{book.Book.Title}': {ex.Message}");
                                }

                                freshCover = null;
                            }
                        }
                    }

                    metadataRequest = new TtsuMetadataImportRequest(freshSelection, ExternalCover: freshCover);
                }

                requests.Add(new(
                    book.Book,
                    input.Mode == TtsuImportMode.Create ? null : input.TargetId,
                    Resolutions(input),
                    review.Fingerprint,
                    input.OrphanLogIds,
                    input.ProgressChoice,
                    metadataRequest));
            }

            receipt = await _imports.ApplyAsync(batch.Id, requests, cancellationToken);
            batchStore.Remove(batch.Id);
            return Success(receipt);
        }
        catch (TtsuImportReviewRequiredException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await PopulateAsync(batch, true, cancellationToken);
            return Page();
        }
    }

    public IActionResult OnPostCancel()
    {
        batchStore.Remove(BatchId);
        return RedirectToPage();
    }

    private bool TryBatch(out TtsuImportBatch batch)
    {
        if (BatchId != Guid.Empty && batchStore.TryGet(BatchId, out batch)) return true;
        batch = null!;
        ModelState.AddModelError(string.Empty, "This import preview has expired. Choose the TTSU folder again.");
        BatchId = Guid.Empty;
        return false;
    }

    private readonly record struct TargetProtectionInfo(bool HasBinding, bool HasJitenLink, bool HasCover);

    private static (bool IsEligible, string? IneligibilityReason) EvaluateTargetProtection(
        TtsuImportMode mode,
        Guid? targetId,
        IReadOnlyDictionary<Guid, TargetProtectionInfo> protections)
    {
        if (mode == TtsuImportMode.Create)
        {
            return (true, null);
        }

        if (targetId is null)
        {
            return (false, "Choose an existing book or create a new copy to apply metadata.");
        }

        if (!protections.TryGetValue(targetId.Value, out var info))
        {
            return (false, "The selected target is no longer available.");
        }

        if (info.HasBinding)
        {
            return (false, "Target already has a confirmed TTSU binding.");
        }
        if (info.HasJitenLink)
        {
            return (false, "Target is already linked to Jiten.");
        }
        if (info.HasCover)
        {
            return (false, "Target already has an existing cover.");
        }

        return (true, null);
    }

    private async Task PopulateAsync(
        TtsuImportBatch batch,
        bool preserve,
        CancellationToken cancellationToken,
        bool? autoSelectHighConfidence = null)
    {
        BatchId = batch.Id;
        EnrichmentTotalCount = batch.Books.Count;
        EnrichmentCompletedCount = batch.Books.Count(b => b.EnrichmentState == TtsuEnrichmentAttemptState.Completed);
        IsEnrichmentActive = batch.Books.Any(b => b.EnrichmentState is TtsuEnrichmentAttemptState.Pending or TtsuEnrichmentAttemptState.InProgress);

        Targets = await _imports.GetTargetsAsync(cancellationToken);
        Orphans = (await _imports.GetOrphansAsync(cancellationToken)).Select(x => new TtsuOrphanViewModel(x.Id, x.Date, x.CharactersRead, x.TimeSpentMinutes)).ToList();
        var posted = preserve ? Selections.GroupBy(x => x.BookKey).ToDictionary(x => x.Key, x => x.First()) : [];

        var targetIdsToCheck = new HashSet<Guid>();
        var matches = new Dictionary<Guid, TtsuMatch>();
        foreach (var item in batch.Books)
        {
            var match = await _imports.MatchAsync(item.Book, cancellationToken);
            matches[item.Key] = match;
            var postedTarget = posted.GetValueOrDefault(item.Key)?.TargetId;
            if (postedTarget.HasValue)
            {
                targetIdsToCheck.Add(postedTarget.Value);
            }
            else
            {
                if (match.WorkId.HasValue)
                {
                    targetIdsToCheck.Add(match.WorkId.Value);
                }
            }
        }

        var protections = await dbContext.MediaWorks
            .AsNoTracking()
            .Where(w => targetIdsToCheck.Contains(w.Id))
            .Select(w => new
            {
                w.Id,
                w.HasJitenLink,
                HasCover = w.CoverUrl != null,
                HasBinding = dbContext.TtsuBindings.Any(b => b.MediaWorkId == w.Id)
            })
            .ToDictionaryAsync(
                w => w.Id,
                w => new TargetProtectionInfo(w.HasBinding, w.HasJitenLink, w.HasCover),
                cancellationToken);

        var books = new List<TtsuBookPreviewViewModel>();
        Selections = [];
        foreach (var item in batch.Books)
        {
            var match = matches[item.Key];
            var postedInput = posted.GetValueOrDefault(item.Key);
            var input = postedInput ?? new TtsuBookSelectionInput
            {
                BookKey = item.Key,
                Selected = true,
                TargetId = match.WorkId,
                Mode = match.WorkId is null && !match.IsAmbiguous ? TtsuImportMode.Create : TtsuImportMode.Merge
            };
            var plan = await _imports.PreviewAsync(item.Book, input.Mode == TtsuImportMode.Create ? null : input.TargetId,
                Resolutions(input), input.OrphanLogIds, cancellationToken, input.ProgressChoice);
            if (!Enum.IsDefined(input.Mode) || input.Mode == TtsuImportMode.Merge && input.TargetId is null)
                plan = plan with { Error = "Choose a target book or create a new copy." };
            var choices = Resolutions(input);
            input.Days = plan.Days.Where(x => x.ReviewReason is not null).Select(x => new TtsuDayResolutionInput
            { Date = x.Date, Choice = choices.GetValueOrDefault(x.Date) ?? string.Empty }).ToList();

            var (isEligible, ineligibilityReason) = EvaluateTargetProtection(input.Mode, input.TargetId, protections);

            if (item.Enrichment is not null)
            {
                var postedCandidate = postedInput?.CandidateKey is Guid postedCandidateKey
                    ? item.Enrichment.Candidates.SingleOrDefault(c => c.Key == postedCandidateKey)
                    : null;

                if (preserve && postedInput?.CandidateKey is not null &&
                    (postedCandidate is null || !postedCandidate.IsSelectable))
                {
                    ModelState.AddModelError(
                        string.Empty,
                        "The metadata selection is invalid. Choose one of this book's available candidates.");
                }

                if (!isEligible)
                {
                    input.CandidateKey = null;
                }
                else if (!preserve || autoSelectHighConfidence == true)
                {
                    input.CandidateKey = item.Enrichment.Confidence == MatchConfidence.High
                        ? item.Enrichment.Candidates.FirstOrDefault(c => c.IsTopCandidate && c.IsSelectable)?.Key
                        : null;
                }
                else
                {
                    input.CandidateKey = postedCandidate?.IsSelectable == true
                        ? postedCandidate.Key
                        : null;
                }
            }
            else
            {
                input.CandidateKey = null;
            }

            input.ReviewToken = Guid.NewGuid();
            batch.Reviews[input.ReviewToken] = new(item.Key, plan.Fingerprint, input.CandidateKey, input.SelectedCoverKey);

            TtsuBookEnrichmentViewModel? enrichmentVm = null;
            if (item.Enrichment is not null)
            {
                var badge = ResolveBadge(item.Enrichment);
                var candidateVms = item.Enrichment.Candidates.Select(c =>
                {
                    var selectedOption = c.CoverOptions?.FirstOrDefault(o => o.SelectionKey == input.SelectedCoverKey)
                                         ?? c.CoverOptions?.FirstOrDefault();

                    var hasExternal = selectedOption is not null || c.GoogleCover is not null;
                    var provider = selectedOption?.Provider ?? (c.GoogleCover?.Provider ?? ExternalCoverProvider.GoogleBooks);
                    var isOl = provider == ExternalCoverProvider.OpenLibrary;
                    var coverUrl = selectedOption?.CoverUrl ?? (c.GoogleCover is not null ? c.GoogleCover.CoverUrl : c.Candidate.CoverUrl);
                    var attributionLink = selectedOption?.AttributionUrl ?? c.GoogleCover?.AttributionLink;
                    var volumeId = selectedOption?.ProviderItemId ?? c.GoogleCover?.VolumeId;
                    var isLowRes = selectedOption?.IsLowResolution ?? (c.GoogleCover?.IsLowResolution ?? false);

                    string coverLabel;
                    if (selectedOption is not null)
                    {
                        coverLabel = isOl ? "Open Library cover" : "Google Books volume cover";
                    }
                    else if (c.GoogleCover is not null)
                    {
                        coverLabel = isOl
                            ? (isLowRes ? "Low-resolution Open Library cover" : "Open Library cover")
                            : (isLowRes ? "Low-resolution Google Books cover" : "Google Books volume cover");
                    }
                    else
                    {
                        coverLabel = ResolveCoverEvidenceLabel(c.Candidate.CoverEvidence);
                    }

                    string? dimensionsLabel = null;
                    if (selectedOption is not null)
                    {
                        dimensionsLabel = $"{selectedOption.Width}×{selectedOption.Height}";
                    }
                    else if (c.GoogleCover?.Width > 0 && c.GoogleCover?.Height > 0)
                    {
                        dimensionsLabel = $"{c.GoogleCover.Width}×{c.GoogleCover.Height}";
                    }

                    var evidence = hasExternal
                        ? c.Evidence.Concat(selectedOption?.Evidence ?? c.GoogleCover!.Evidence).ToList()
                        : c.Evidence;

                    var coverWarning = c.GoogleCoverStatus is GoogleBooksMatchStatus.NoMatch or
                        GoogleBooksMatchStatus.Ambiguous or GoogleBooksMatchStatus.InvalidImage or
                        GoogleBooksMatchStatus.RateLimited ||
                        c.GoogleCoverStatus == GoogleBooksMatchStatus.Unavailable &&
                        !string.Equals(
                            c.GoogleCoverWarning,
                            "Google Books API key is not configured.",
                            StringComparison.Ordinal)
                            ? c.GoogleCoverWarning
                            : null;

                    var isLowConfidence = c.IsSelectable && c.Score < JitenCandidateScorer.ReviewConfidenceThreshold;
                    var googleStatusMessage = ResolveGoogleStatusMessage(c, IsGoogleBooksConfigured);

                    return new TtsuCandidateChoiceViewModel(
                        c.Key,
                        c.Candidate.DeckId,
                        c.Candidate.SubdeckId,
                        c.Candidate.DisplayTitle,
                        c.Candidate.RomajiTitle,
                        c.Candidate.EnglishTitle,
                        c.Candidate.CharacterCount,
                        c.Score,
                        coverUrl,
                        c.Candidate.CoverEvidence,
                        coverLabel,
                        evidence,
                        c.IsTopCandidate,
                        c.IsSelectable,
                        volumeId,
                        attributionLink,
                        hasExternal,
                        coverWarning,
                        googleStatusMessage,
                        isLowConfidence,
                        c.IsDisqualified,
                        c.DisqualificationReason,
                        c.GoogleCover?.Proof ?? GoogleBooksIdentityProof.None,
                        isLowRes,
                        c.CoverOptions,
                        input.SelectedCoverKey ?? selectedOption?.SelectionKey,
                        hasExternal ? provider : null,
                        dimensionsLabel);
                }).ToList();

                enrichmentVm = new TtsuBookEnrichmentViewModel(
                    badge,
                    ResolveBadgeLabel(badge),
                    item.Enrichment.Evidence,
                    item.Enrichment.Warnings,
                    candidateVms,
                    input.CandidateKey,
                    isEligible,
                    ineligibilityReason,
                    item.Enrichment.OmittedPlausibleCount,
                    item.Enrichment.FilteredIncompatibleCount);
            }

            books.Add(new(item.Key, item.Book.Title, item.Book.FolderHint, match.Reason, plan, enrichmentVm));
            Selections.Add(input);
        }
        Books = books;
        // Render newly generated review tokens and normalized choices, not the posted values.
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Selections[", StringComparison.Ordinal)).ToList())
            ModelState.Remove(key);
    }

    public static bool IsExactIdentityCandidate(
        ScoredCandidate scored,
        StructuredVolume? ttsuVolume)
    {
        if (scored.IsDisqualified) return false;
        if (scored.TitleScore != JitenCandidateScorer.MaxTitleScore || scored.IsPartialTitleMatch) return false;
        if (scored.VolumeScore != JitenCandidateScorer.ExactVolumeScore) return false;

        var candVol = scored.CandidateVolume;
        if (ttsuVolume is null || candVol is null) return false;
        if (ttsuVolume.Kind != VolumeKind.Standard || candVol.Kind != VolumeKind.Standard) return false;
        if (!ttsuVolume.Number.HasValue || !candVol.Number.HasValue || ttsuVolume.Number != candVol.Number) return false;
        if (!ttsuVolume.Matches(candVol) || ttsuVolume.ConflictsWith(candVol)) return false;

        if (scored.Candidate.IsSubdeck)
        {
            if (string.IsNullOrWhiteSpace(scored.Candidate.ParentOriginalTitle) &&
                string.IsNullOrWhiteSpace(scored.Candidate.ParentRomajiTitle) &&
                string.IsNullOrWhiteSpace(scored.Candidate.ParentEnglishTitle))
            {
                return false;
            }
        }
        else if (scored.Candidate.IsStandalone)
        {
            if (string.IsNullOrWhiteSpace(scored.Candidate.OriginalTitle) &&
                string.IsNullOrWhiteSpace(scored.Candidate.RomajiTitle) &&
                string.IsNullOrWhiteSpace(scored.Candidate.EnglishTitle))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return true;
    }

    public static string ResolveGoogleStatusMessage(TtsuEnrichmentCandidate candidate, bool isGoogleBooksConfigured)
    {
        if (candidate.CoverOptions is { Count: > 1 })
        {
            return "Several exact cover editions require selection";
        }

        if (candidate.GoogleCover?.Provider == ExternalCoverProvider.OpenLibrary)
        {
            if (candidate.GoogleCover.IsLowResolution)
            {
                return "Low-resolution Open Library cover";
            }

            return "High-resolution Open Library cover selected";
        }

        if (!isGoogleBooksConfigured || candidate.GoogleCoverStatus == GoogleBooksMatchStatus.NotConfigured)
        {
            return "Google Books is not configured";
        }

        if (candidate.GoogleCover is not null || candidate.GoogleCoverStatus == GoogleBooksMatchStatus.Matched)
        {
            if (candidate.GoogleCover?.IsLowResolution == true)
            {
                return "Low-resolution Google Books cover";
            }

            if (candidate.GoogleCover?.Proof == GoogleBooksIdentityProof.CrossQueryInferredVolume)
            {
                return "Google Books cover found — volume inferred from two matching searches";
            }

            return "Google Books cover found — explicit volume match";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.NoMatch)
        {
            if (!string.IsNullOrWhiteSpace(candidate.GoogleCoverWarning) &&
                candidate.GoogleCoverWarning.Contains("exact title found, but Google omitted the volume number and cross-query proof was insufficient", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Books checked — exact title found, but Google omitted the volume number and cross-query proof was insufficient";
            }

            return "Jiten matched, external cover missing";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.Ambiguous)
        {
            if (!string.IsNullOrWhiteSpace(candidate.GoogleCoverWarning) &&
                candidate.GoogleCoverWarning.Contains("multiple markerless editions remained ambiguous", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Books checked — multiple markerless editions remained ambiguous";
            }

            return "Google Books checked — multiple editions were ambiguous";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.InvalidImage)
        {
            return "Google Books image was too small or invalid";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.TimedOut)
        {
            return "Google Books lookup timed out";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.RateLimited)
        {
            return "Google Books rate limit exceeded";
        }

        if (candidate.GoogleCoverStatus == GoogleBooksMatchStatus.Unavailable)
        {
            return "Google Books is unavailable";
        }

        if (!candidate.IsExactIdentity)
        {
            return "Google Books not checked — no exact Jiten title-and-volume candidate";
        }

        return "Select a candidate and refresh review to check its cover";
    }

    private static TtsuBookEnrichment CreateEnrichment(JitenMatchOutcome outcome)
    {
        var confidence = outcome.Result?.Confidence ?? MatchConfidence.None;
        var allScored = outcome.Result?.Candidates ?? [];
        var ttsuVolume = outcome.Result?.ParsedTitle?.Volume;

        var plausible = allScored.Where(s => !s.IsDisqualified).ToList();

        const int maxVisibleCandidates = 3;
        var visibleScored = plausible.Take(maxVisibleCandidates).ToList();
        var omittedPlausibleCount = Math.Max(0, plausible.Count - maxVisibleCandidates);
        var filteredIncompatibleCount = outcome.Result?.FilteredIncompatibleCount ?? 0;

        var candidates = visibleScored
            .Select((scored, idx) => new TtsuEnrichmentCandidate(
                Guid.NewGuid(),
                scored.Candidate,
                scored.TotalScore,
                scored.Evidence,
                idx == 0 && confidence == MatchConfidence.High,
                outcome.Status == JitenMatchStatus.Matched,
                GoogleCover: null,
                GoogleCoverStatus: null,
                GoogleCoverWarning: null,
                IsDisqualified: false,
                DisqualificationReason: null,
                IsExactIdentity: IsExactIdentityCandidate(scored, ttsuVolume)))
            .ToList();

        return new TtsuBookEnrichment(
            outcome.Status,
            confidence,
            outcome.Result?.Evidence ?? [],
            outcome.Warnings,
            candidates,
            OmittedPlausibleCount: omittedPlausibleCount,
            FilteredIncompatibleCount: filteredIncompatibleCount);
    }

    private static TtsuEnrichmentBadge ResolveBadge(TtsuBookEnrichment enrichment)
    {
        if (enrichment.Status is JitenMatchStatus.Unavailable or JitenMatchStatus.RateLimited)
        {
            return TtsuEnrichmentBadge.JitenUnavailable;
        }

        if (enrichment.Status == JitenMatchStatus.NoCandidates)
        {
            return TtsuEnrichmentBadge.NoSafeMatch;
        }

        if (enrichment.Status == JitenMatchStatus.Matched)
        {
            if (enrichment.Confidence == MatchConfidence.High)
            {
                return TtsuEnrichmentBadge.AutoMatched;
            }

            if (enrichment.Confidence == MatchConfidence.Review && enrichment.Candidates.Count > 0)
            {
                return TtsuEnrichmentBadge.NeedsReview;
            }

            return TtsuEnrichmentBadge.NoSafeMatch;
        }

        return TtsuEnrichmentBadge.NoSafeMatch;
    }

    private static string ResolveBadgeLabel(TtsuEnrichmentBadge badge) => badge switch
    {
        TtsuEnrichmentBadge.AutoMatched => "Auto-matched",
        TtsuEnrichmentBadge.NeedsReview => "Needs review",
        TtsuEnrichmentBadge.NoSafeMatch => "No safe match",
        TtsuEnrichmentBadge.JitenUnavailable => "Jiten unavailable",
        _ => string.Empty
    };

    private static string ResolveCoverEvidenceLabel(JitenCoverEvidence evidence) => evidence switch
    {
        JitenCoverEvidence.Specific => "Direct cover",
        JitenCoverEvidence.ParentFallback => "Series cover fallback",
        JitenCoverEvidence.None => "No cover",
        _ => "No cover"
    };

    private static Dictionary<DateOnly, string> Resolutions(TtsuBookSelectionInput input) =>
        input.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First().Choice);

    private IActionResult Success(TtsuImportReceipt receipt)
    {
        var metadataSuffix = receipt.MetadataLinks > 0 || receipt.MetadataSkips > 0
            ? $", {receipt.MetadataLinks} metadata linked, {receipt.MetadataSkips} metadata skipped"
            : string.Empty;

        TempData["LibraryNotice"] = receipt.AddedDays == 0 && receipt.UpdatedDays == 0 && receipt.ProgressUpdates == 0
            ? $"Statistics and progress are already up to date. {receipt.StaleDays} older days skipped{metadataSuffix}."
            : $"Imported {receipt.Books} books: {receipt.AddedDays} new days, {receipt.UpdatedDays} updated days, {receipt.UnchangedDays} unchanged days, {receipt.StaleDays} older days skipped, {receipt.ProgressUpdates} progress updates{metadataSuffix}.";
        return RedirectToPage("/Library/Index");
    }

    private async Task ResolveGoogleCoverForCandidateAsync(
        TtsuImportBatchBook book,
        TtsuEnrichmentCandidate candidate,
        CancellationToken budgetToken,
        CancellationToken requestToken = default)
    {
        if (googleBooksCoverService is null) return;

        var parsedTtsu = MediaTitleParser.ParseTitle(book.Book.Title);
        var subdeckVolume = MediaTitleParser.ExtractChildVolume(candidate.Candidate.OriginalTitle, candidate.Candidate.IsSubdeck);

        var context = new GoogleCoverLookupContext(
            book.Book.Title,
            parsedTtsu,
            parsedTtsu.Volume,
            candidate.Candidate.ParentOriginalTitle,
            candidate.Candidate.ParentRomajiTitle,
            candidate.Candidate.ParentEnglishTitle,
            candidate.Candidate.OriginalTitle,
            subdeckVolume,
            candidate.Candidate.IsStandalone ? candidate.Candidate.OriginalTitle : null,
            candidate.Candidate.IsStandalone ? candidate.Candidate.RomajiTitle : null,
            candidate.Candidate.IsStandalone ? candidate.Candidate.EnglishTitle : null,
            candidate.Candidate.IsSubdeck,
            candidate.Candidate.IsStandalone,
            candidate.Candidate.DeckId,
            candidate.Candidate.SubdeckId,
            candidate.IsExactIdentity);

        try
        {
            var result = await googleBooksCoverService.ResolveCoverAsync(context, budgetToken);
            ApplyGoogleCoverResult(book, candidate, result);
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            var timedOutResult = GoogleBooksCoverMatchResult.CreateTimedOut("Google Books lookup timed out.");
            ApplyGoogleCoverResult(book, candidate, timedOutResult);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Google Books cover lookup failed for '{Title}': {Message}", book.Book.Title, ex.Message);
        }
    }

    private static void ApplyGoogleCoverResult(
        TtsuImportBatchBook book,
        TtsuEnrichmentCandidate candidate,
        GoogleBooksCoverMatchResult result)
    {
        var updatedCandidate = candidate with
        {
            GoogleCover = result.IsMatched
                ? new TtsuCandidateGoogleCover(
                    result.VolumeId!,
                    result.CoverUrl!,
                    result.AttributionLink,
                    result.Evidence ?? [],
                    result.Proof,
                    result.IsLowResolution,
                    Provider: result.Provider)
                : null,
            GoogleCoverStatus = result.Status,
            GoogleCoverWarning = result.Warning,
            CoverOptions = result.EditionOptions
        };

        if (book.Enrichment is not null)
        {
            var candidatesList = book.Enrichment.Candidates.ToList();
            var index = candidatesList.FindIndex(c => c.Key == candidate.Key);
            if (index >= 0)
            {
                candidatesList[index] = updatedCandidate;
                book.Enrichment = book.Enrichment with { Candidates = candidatesList };
            }
        }
    }

    private async Task ResolveGoogleCoversWithinBudgetAsync(
        IEnumerable<(TtsuImportBatchBook Book, TtsuEnrichmentCandidate Candidate)> targets,
        CancellationToken requestCancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        budget.CancelAfter(CoverEnrichmentTimeBudget);

        try
        {
            // The scoped EF DbContext is not thread-safe. Complete all database
            // eligibility checks serially before parallelizing only HTTP work.
            var eligibleTargets = new List<(TtsuImportBatchBook Book, TtsuEnrichmentCandidate Candidate)>();
            foreach (var target in targets)
            {
                if (await CanResolveGoogleCoverAsync(target.Book, budget.Token))
                {
                    eligibleTargets.Add(target);
                }
            }

            await Task.WhenAll(eligibleTargets.Select(target =>
                ResolveGoogleCoverForCandidateAsync(target.Book, target.Candidate, budget.Token, requestCancellationToken)));
        }
        catch (OperationCanceledException) when (!requestCancellationToken.IsCancellationRequested)
        {
            // Cover enrichment is optional. The ordinary reading/Jiten preview
            // remains usable when the internal cover budget expires.
        }
    }

    private async Task<bool> CanResolveGoogleCoverAsync(
        TtsuImportBatchBook book,
        CancellationToken cancellationToken)
    {
        var match = await _imports.MatchAsync(book.Book, cancellationToken);
        if (match.WorkId is not Guid targetId) return true;

        var targetState = await dbContext.MediaWorks
            .AsNoTracking()
            .Where(work => work.Id == targetId)
            .Select(work => new
            {
                work.HasCover,
                work.HasJitenLink,
                HasBinding = dbContext.TtsuBindings.Any(binding => binding.MediaWorkId == work.Id)
            })
            .SingleOrDefaultAsync(cancellationToken);

        return targetState is not ({ HasCover: true } or { HasJitenLink: true } or { HasBinding: true });
    }
}
