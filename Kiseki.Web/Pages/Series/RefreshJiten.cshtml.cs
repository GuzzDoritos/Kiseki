using System.Net;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Series;

public sealed class RefreshJitenModel(
    ImmersionDbContext context,
    JitenCatalogueReconciliationService reconciliationService,
    ILogger<RefreshJitenModel> logger) : PageModel
{
    public SeriesRefreshJitenViewModel ViewModel { get; private set; } = null!;

    public string? Notice => TempData["LibraryNotice"] as string;

    [BindProperty(SupportsGet = true)]
    public Guid SeriesId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? OperationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool? Replayed { get; set; }

    [BindProperty]
    public int? DeckId { get; set; }

    [BindProperty]
    public Guid ApplyReviewId { get; set; }

    [BindProperty]
    public string ApplyExpectedFingerprint { get; set; } = string.Empty;

    [BindProperty]
    public Guid ApplyOperationId { get; set; }

    [BindProperty]
    public List<ProposalChoiceInput> Choices { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(
        Guid? seriesId,
        Guid? operationId,
        bool? replayed,
        CancellationToken cancellationToken)
    {
        var targetSeriesId = seriesId ?? SeriesId;
        if (targetSeriesId == Guid.Empty)
        {
            return NotFound();
        }

        var series = await context.MediaSeries.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == targetSeriesId, cancellationToken);

        if (series is null)
        {
            return NotFound();
        }

        if (series.MediaType != MediaType.Book)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "Jiten catalogue refresh currently supports book series only.");
            return Page();
        }

        var targetOperationId = operationId ?? OperationId;
        if (targetOperationId.HasValue && targetOperationId.Value != Guid.Empty)
        {
            var receipt = await reconciliationService.GetReceiptAsync(
                targetOperationId.Value, series.Id, cancellationToken);
            if (receipt is not null)
            {
                var receiptVm = new JitenRefreshReceiptViewModel(
                    receipt.Id,
                    receipt.MediaSeriesId,
                    receipt.JitenDeckId,
                    receipt.AddedInstallments,
                    receipt.LinkedIdentities,
                    receipt.UpdatedInstallments,
                    receipt.MarkedMissing,
                    receipt.Ignored,
                    receipt.CompletedAtUtc,
                    replayed ?? Replayed ?? false);

                ViewModel = new SeriesRefreshJitenViewModel(
                    series.Id,
                    series.Title,
                    series.MediaType,
                    series.JitenDeckId,
                    RefreshJitenUiState.Receipt,
                    Receipt: receiptVm);
                return Page();
            }

            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "That refresh receipt was not found for this series.");
            return Page();
        }

        ViewModel = new SeriesRefreshJitenViewModel(
            series.Id,
            series.Title,
            series.MediaType,
            series.JitenDeckId,
            RefreshJitenUiState.DeckPrompt);

        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(
        Guid seriesId,
        int? deckId,
        CancellationToken cancellationToken)
    {
        var targetSeriesId = seriesId != Guid.Empty ? seriesId : SeriesId;
        if (targetSeriesId == Guid.Empty)
        {
            return NotFound();
        }

        var series = await context.MediaSeries.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == targetSeriesId, cancellationToken);

        if (series is null)
        {
            return NotFound();
        }

        if (series.MediaType != MediaType.Book)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "Jiten catalogue refresh currently supports book series only.");
            return Page();
        }

        int targetDeckId;
        if (series.JitenDeckId.HasValue)
        {
            targetDeckId = series.JitenDeckId.Value;
        }
        else
        {
            var chosenDeck = deckId ?? DeckId;
            if (!chosenDeck.HasValue || chosenDeck.Value <= 0)
            {
                ViewModel = new SeriesRefreshJitenViewModel(
                    series.Id,
                    series.Title,
                    series.MediaType,
                    series.JitenDeckId,
                    RefreshJitenUiState.DeckPrompt,
                    ErrorMessage: "Enter a valid positive Jiten deck ID.");
                return Page();
            }
            targetDeckId = chosenDeck.Value;
        }

        try
        {
            var review = await reconciliationService.PreviewAsync(series.Id, targetDeckId, cancellationToken);
            var localCandidates = await LoadCandidateInstallmentsAsync(
                series.Id, review.Proposals, cancellationToken);
            var proposalVms = BuildProposalViewModels(review.Proposals, localCandidates, null);

            var operationId = Guid.NewGuid();
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId ?? targetDeckId,
                RefreshJitenUiState.Reviewing,
                ReviewId: review.ReviewId,
                Fingerprint: review.Fingerprint,
                OperationId: operationId,
                ExpiresAtUtc: review.ExpiresAtUtc,
                IsCompleteFetch: review.IsCompleteFetch,
                ExpectedItems: review.ExpectedItems,
                RetrievedItems: review.RetrievedItems,
                Warning: review.Warning,
                Proposals: proposalVms);

            return Page();
        }
        catch (JitenCatalogueReviewRequiredException ex)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: ex.Message,
                IsStaleReview: true);
            return Page();
        }
        catch (JitenHttpException ex)
        {
            var retrySecs = ex.RetryAfter.HasValue ? (int)Math.Ceiling(ex.RetryAfter.Value.TotalSeconds) : (int?)null;
            var msg = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? $"Jiten rate limit reached. {(retrySecs.HasValue ? $"Retry after {retrySecs.Value} seconds." : "Wait a moment and try again.")}"
                : $"Jiten API error: {ex.Message}";

            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: msg,
                RetryAfterSeconds: retrySecs);
            return Page();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "Jiten took too long to respond. Try again.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure while previewing Jiten catalogue for series {SeriesId}.", series.Id);
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "The Jiten preview could not be loaded. No catalogue changes were made.");
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApplyAsync(
        Guid seriesId,
        Guid applyReviewId,
        string applyExpectedFingerprint,
        Guid applyOperationId,
        List<ProposalChoiceInput> choices,
        CancellationToken cancellationToken)
    {
        var targetSeriesId = seriesId != Guid.Empty ? seriesId : SeriesId;
        if (targetSeriesId == Guid.Empty)
        {
            return NotFound();
        }

        var series = await context.MediaSeries.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == targetSeriesId, cancellationToken);

        if (series is null)
        {
            return NotFound();
        }

        if (series.MediaType != MediaType.Book)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "Jiten catalogue refresh currently supports book series only.");
            return Page();
        }

        var submittedChoices = choices ?? Choices ?? [];
        var reviewId = applyReviewId != Guid.Empty ? applyReviewId : ApplyReviewId;
        var fingerprint = !string.IsNullOrWhiteSpace(applyExpectedFingerprint) ? applyExpectedFingerprint : ApplyExpectedFingerprint;
        var operationId = applyOperationId != Guid.Empty ? applyOperationId : ApplyOperationId;

        // Check if already committed for idempotent replay
        var existingReceipt = operationId == Guid.Empty
            ? null
            : await reconciliationService.GetReceiptAsync(operationId, series.Id, cancellationToken);
        if (existingReceipt is not null)
        {
            TempData["LibraryNotice"] = "Replayed previous Jiten refresh results.";
            return RedirectToPage(new { seriesId = series.Id, operationId = existingReceipt.Id, replayed = true });
        }

        var domainChoices = submittedChoices.Select(c => new JitenCatalogueChoice(
            c.ProposalId,
            c.Action,
            c.Action == JitenCatalogueChoiceAction.LinkExisting ? c.InstallmentId : null
        )).ToList();

        try
        {
            var approved = reconciliationService.Approve(reviewId, fingerprint, domainChoices);
            var result = await reconciliationService.ApplyAsync(
                operationId, series.Id, reviewId, approved.ApprovalFingerprint!, cancellationToken);

            TempData["LibraryNotice"] = "Successfully refreshed catalogue from Jiten.";
            return RedirectToPage(new { seriesId = series.Id, operationId = result.OperationId, replayed = false });
        }
        catch (JitenCatalogueReviewRequiredException ex)
        {
            var activeReview = ex.CanRetryCurrentReview
                ? reconciliationService.GetActiveReview(reviewId)
                : null;

            if (activeReview is not null &&
                activeReview.MediaSeriesId == series.Id &&
                activeReview.Fingerprint == fingerprint)
            {
                // Preserve review screen for choice validation errors
                var localCandidates = await LoadCandidateInstallmentsAsync(
                    series.Id, activeReview.Proposals, cancellationToken);
                var proposalVms = BuildProposalViewModels(activeReview.Proposals, localCandidates, submittedChoices);

                ViewModel = new SeriesRefreshJitenViewModel(
                    series.Id,
                    series.Title,
                    series.MediaType,
                    activeReview.JitenDeckId,
                    RefreshJitenUiState.Reviewing,
                    ReviewId: activeReview.ReviewId,
                    Fingerprint: activeReview.Fingerprint,
                    OperationId: operationId,
                    ExpiresAtUtc: activeReview.ExpiresAtUtc,
                    IsCompleteFetch: activeReview.IsCompleteFetch,
                    ExpectedItems: activeReview.ExpectedItems,
                    RetrievedItems: activeReview.RetrievedItems,
                    Warning: activeReview.Warning,
                    Proposals: proposalVms,
                    ErrorMessage: ex.Message);

                return Page();
            }

            // Evidence changed, expired, or review store restarted
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: ex.Message,
                IsStaleReview: true);

            return Page();
        }
        catch (JitenHttpException ex)
        {
            var retrySecs = ex.RetryAfter.HasValue ? (int)Math.Ceiling(ex.RetryAfter.Value.TotalSeconds) : (int?)null;
            var msg = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? $"Jiten rate limit reached. {(retrySecs.HasValue ? $"Retry after {retrySecs.Value} seconds." : "Wait a moment and try again.")}"
                : $"Jiten API error: {ex.Message}";

            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: msg,
                RetryAfterSeconds: retrySecs);
            return Page();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "The refresh operation timed out. You may safely retry with the same operation ID.");
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure while applying Jiten catalogue review for series {SeriesId}.",
                series.Id);
            ViewModel = new SeriesRefreshJitenViewModel(
                series.Id,
                series.Title,
                series.MediaType,
                series.JitenDeckId,
                RefreshJitenUiState.Error,
                ErrorMessage: "The Jiten refresh could not be applied. No unreviewed changes were accepted.");
            return Page();
        }
    }

    private async Task<IReadOnlyList<CandidateInstallmentOption>> LoadCandidateInstallmentsAsync(
        Guid seriesId,
        IReadOnlyList<JitenCatalogueProposal> proposals,
        CancellationToken cancellationToken)
    {
        var reviewedInstallmentIds = proposals
            .SelectMany(proposal => proposal.CandidateInstallmentIds.Concat(
                proposal.ExistingInstallmentId is Guid existingId ? [existingId] : []))
            .Distinct()
            .ToArray();
        if (reviewedInstallmentIds.Length == 0)
        {
            return [];
        }

        return await context.MediaInstallments.AsNoTracking()
            .Where(i => i.MediaSeriesId == seriesId && reviewedInstallmentIds.Contains(i.Id))
            .OrderBy(i => i.OrderKey)
            .ThenBy(i => i.Id)
            .Select(i => new CandidateInstallmentOption(
                i.Id,
                i.TitleOverride ?? i.CanonicalTitle ?? i.LegacyTitle ?? "Untitled Installment",
                i.OrderKey))
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<JitenReviewProposalViewModel> BuildProposalViewModels(
        IReadOnlyList<JitenCatalogueProposal> proposals,
        IReadOnlyList<CandidateInstallmentOption> localCandidates,
        IReadOnlyList<ProposalChoiceInput>? userChoices)
    {
        var localMap = localCandidates.ToDictionary(c => c.Id);
        var choicesMap = userChoices?.ToDictionary(c => c.ProposalId, StringComparer.Ordinal);

        return proposals.Select(p =>
        {
            var candidates = p.CandidateInstallmentIds
                .Where(id => localMap.ContainsKey(id))
                .Select(id => localMap[id])
                .ToList();

            string? existingTitle = null;
            if (p.ExistingInstallmentId.HasValue && localMap.TryGetValue(p.ExistingInstallmentId.Value, out var existing))
            {
                existingTitle = existing.Title;
            }

            var selectedAction = p.RecommendedAction;
            var selectedInstallmentId = selectedAction == JitenCatalogueChoiceAction.LinkExisting
                ? p.CandidateInstallmentIds.FirstOrDefault()
                : (Guid?)null;

            if (choicesMap is not null && choicesMap.TryGetValue(p.ProposalId, out var userChoice))
            {
                selectedAction = userChoice.Action;
                selectedInstallmentId = userChoice.InstallmentId;
            }

            return new JitenReviewProposalViewModel(
                p.ProposalId,
                p.ProviderKey,
                p.Kind,
                p.ProviderItem?.Title ?? p.ProviderKey,
                p.ProviderItem?.CharacterCount,
                p.ProviderItem?.CoverUrl,
                p.ProviderItem?.CoverSource ?? CanonicalCoverSource.None,
                p.ProviderItem?.ReleaseState ?? ReleaseState.Unknown,
                p.ProviderItem?.ReleaseDate,
                p.ProviderItem?.ProviderOrder ?? 0,
                p.ExistingInstallmentId,
                existingTitle,
                candidates,
                p.AllowedActions,
                p.RecommendedAction,
                selectedAction,
                selectedInstallmentId,
                p.HasManualConflict,
                p.HasUncertainOrder,
                p.Message);
        }).ToList();
    }

}
