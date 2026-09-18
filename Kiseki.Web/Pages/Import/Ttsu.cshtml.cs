using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;
using Kiseki.Web.Models;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Import;

[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueCountLimit = 100_000)]
public sealed class TtsuModel(
    TtsuDataLoader dataLoader,
    ITtsuImportBatchStore batchStore,
    ImmersionDbContext dbContext,
    IJitenMatchService matchService,
    IJitenSelectionResolver selectionResolver) : PageModel
{
    private const long MaxRequestBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan EnrichmentTimeBudget = TimeSpan.FromSeconds(8);
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
            var requests = batch.Books.Select(item => new JitenMatchRequest
            {
                CorrelationId = item.Key,
                RawTitle = item.Book.Title,
                AuthoritativeTtsuTotal = TtsuProgressNormalizer.ResolveAuthoritativeTotal(item.Book)
            }).ToList();

            var outcomes = await matchService.MatchBatchAsync(requests, EnrichmentTimeBudget, cancellationToken);
            var outcomeByCorrelationId = outcomes.ToDictionary(o => o.CorrelationId);

            foreach (var item in batch.Books)
            {
                if (outcomeByCorrelationId.TryGetValue(item.Key, out var outcome))
                {
                    item.Enrichment = CreateEnrichment(outcome);
                }
            }
        }

        await PopulateAsync(batch, false, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken cancellationToken)
    {
        if (!TryBatch(out var batch)) return Page();
        await PopulateAsync(batch, true, cancellationToken);
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
                    review.CandidateKey != input.CandidateKey)
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

                    metadataRequest = new TtsuMetadataImportRequest(freshSelection);
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

    private async Task PopulateAsync(TtsuImportBatch batch, bool preserve, CancellationToken cancellationToken)
    {
        BatchId = batch.Id;
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
                HasCover = w.JitenCoverUrl != null,
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
                else if (!preserve)
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
            batch.Reviews[input.ReviewToken] = new(item.Key, plan.Fingerprint, input.CandidateKey);

            TtsuBookEnrichmentViewModel? enrichmentVm = null;
            if (item.Enrichment is not null)
            {
                var badge = ResolveBadge(item.Enrichment);
                var candidateVms = item.Enrichment.Candidates.Select(c => new TtsuCandidateChoiceViewModel(
                    c.Key,
                    c.Candidate.DeckId,
                    c.Candidate.SubdeckId,
                    c.Candidate.DisplayTitle,
                    c.Candidate.RomajiTitle,
                    c.Candidate.EnglishTitle,
                    c.Candidate.CharacterCount,
                    c.Score,
                    c.Candidate.CoverUrl,
                    c.Candidate.CoverEvidence,
                    ResolveCoverEvidenceLabel(c.Candidate.CoverEvidence),
                    c.Evidence,
                    c.IsTopCandidate,
                    c.IsSelectable)).ToList();

                enrichmentVm = new TtsuBookEnrichmentViewModel(
                    badge,
                    ResolveBadgeLabel(badge),
                    item.Enrichment.Evidence,
                    item.Enrichment.Warnings,
                    candidateVms,
                    input.CandidateKey,
                    isEligible,
                    ineligibilityReason);
            }

            books.Add(new(item.Key, item.Book.Title, item.Book.FolderHint, match.Reason, plan, enrichmentVm));
            Selections.Add(input);
        }
        Books = books;
        // Render newly generated review tokens and normalized choices, not the posted values.
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Selections[", StringComparison.Ordinal)).ToList())
            ModelState.Remove(key);
    }

    private static TtsuBookEnrichment CreateEnrichment(JitenMatchOutcome outcome)
    {
        var confidence = outcome.Result?.Confidence ?? MatchConfidence.None;
        var candidates = (outcome.Result?.Candidates ?? [])
            .Select((scored, idx) => new TtsuEnrichmentCandidate(
                Guid.NewGuid(),
                scored.Candidate,
                scored.TotalScore,
                scored.Evidence,
                idx == 0 && !scored.IsDisqualified && confidence is MatchConfidence.High or MatchConfidence.Review,
                outcome.Status == JitenMatchStatus.Matched &&
                    confidence is MatchConfidence.High or MatchConfidence.Review &&
                    !scored.IsDisqualified))
            .ToList();

        return new TtsuBookEnrichment(
            outcome.Status,
            confidence,
            outcome.Result?.Evidence ?? [],
            outcome.Warnings,
            candidates);
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
}
