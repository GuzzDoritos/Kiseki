using System.Net;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Franchises;

public sealed class DetailsModel(
    ImmersionDbContext context,
    FranchiseCatalogueService franchiseService,
    JitenFranchiseTopologyService topologyService,
    ILogger<DetailsModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? OperationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool? Replayed { get; set; }

    public FranchiseDetailsViewModel FranchiseDetails { get; private set; } = null!;
    public IReadOnlyList<AvailableSeriesOption> AvailableSeries { get; private set; } = [];
    public FranchiseTopologyReviewViewModel? TopologyReview { get; private set; }
    public FranchiseTopologyReceiptViewModel? Receipt { get; private set; }

    public string? Notice => TempData["LibraryNotice"] as string;
    public string? Error { get; set; }

    [BindProperty]
    public int? PreviewAnchorDeckId { get; set; }

    [BindProperty]
    public Guid MoveSeriesId { get; set; }

    [BindProperty]
    public Guid UnassignSeriesId { get; set; }

    [BindProperty]
    public Guid ApplyReviewId { get; set; }

    [BindProperty]
    public string ApplyExpectedFingerprint { get; set; } = string.Empty;

    [BindProperty]
    public Guid ApplyOperationId { get; set; }

    [BindProperty]
    public List<FranchiseTopologyChoiceInput> Choices { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        Guid? operationId,
        bool? replayed,
        CancellationToken cancellationToken)
    {
        Id = id;
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var loaded = await LoadDetailsAndAvailableAsync(id, cancellationToken);
        if (!loaded)
        {
            return NotFound();
        }

        PreviewAnchorDeckId = FranchiseDetails.JitenAnchorDeckId;

        var targetOperationId = operationId ?? OperationId;
        if (targetOperationId.HasValue && targetOperationId.Value != Guid.Empty)
        {
            var receipt = await topologyService.GetReceiptAsync(targetOperationId.Value, id, cancellationToken);
            if (receipt is not null)
            {
                Receipt = new FranchiseTopologyReceiptViewModel(
                    receipt.Id,
                    receipt.FranchiseId,
                    receipt.AnchorDeckId,
                    receipt.CreatedSeries,
                    receipt.LinkedSeries,
                    receipt.IgnoredNodes,
                    receipt.UnresolvedNodes,
                    receipt.CompletedAtUtc,
                    replayed ?? Replayed ?? false);
            }
            else
            {
                Error = "The requested topology receipt was not found for this franchise.";
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostMoveSeriesAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (MoveSeriesId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(MoveSeriesId), "Select a series to add.");
            await LoadDetailsAndAvailableAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            await franchiseService.MoveSeriesAsync(
                new MoveSeriesToFranchiseCommand(id, MoveSeriesId),
                cancellationToken);

            TempData["LibraryNotice"] = "Series added to franchise.";
            return RedirectToPage("/Franchises/Details", new { id });
        }
        catch (MediaCatalogConflictException ex)
        {
            Error = ex.Message;
            await LoadDetailsAndAvailableAsync(id, cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostUnassignSeriesAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (UnassignSeriesId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(UnassignSeriesId), "Select a series to unassign.");
            await LoadDetailsAndAvailableAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            await franchiseService.UnassignSeriesAsync(
                new UnassignSeriesFromFranchiseCommand(id, UnassignSeriesId),
                cancellationToken);

            TempData["LibraryNotice"] = "Series unassigned from franchise.";
            return RedirectToPage("/Franchises/Details", new { id });
        }
        catch (MediaCatalogConflictException ex)
        {
            Error = ex.Message;
            await LoadDetailsAndAvailableAsync(id, cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostPreviewTopologyAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (!await LoadDetailsAndAvailableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (!PreviewAnchorDeckId.HasValue || PreviewAnchorDeckId.Value <= 0)
        {
            ModelState.AddModelError(nameof(PreviewAnchorDeckId), "Enter a positive Jiten anchor deck ID.");
            return Page();
        }

        try
        {
            var review = await topologyService.PreviewAsync(id, PreviewAnchorDeckId.Value, cancellationToken);
            await PopulateReviewViewModelAsync(review, cancellationToken);
            return Page();
        }
        catch (JitenFranchiseTopologyReviewRequiredException ex)
        {
            Error = ex.Message;
            return Page();
        }
        catch (JitenHttpException ex)
        {
            Error = ex.StatusCode == HttpStatusCode.TooManyRequests
                ? "Jiten rate limit reached. Please wait before retrying."
                : $"Failed to retrieve franchise graph from Jiten: {ex.Message}";
            return Page();
        }
        catch (OperationCanceledException)
        {
            Error = "The Jiten preview request was cancelled.";
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error previewing franchise graph for deck {DeckId}", PreviewAnchorDeckId);
            Error = "An unexpected error occurred while fetching the franchise graph.";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApplyTopologyAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (!await LoadDetailsAndAvailableAsync(id, cancellationToken))
        {
            return NotFound();
        }

        if (ApplyReviewId == Guid.Empty || string.IsNullOrWhiteSpace(ApplyExpectedFingerprint) || ApplyOperationId == Guid.Empty)
        {
            Error = "Review correlation data is missing. Please preview the graph again.";
            return Page();
        }

        var domainChoices = Choices.Select(c => new JitenFranchiseTopologyChoice(
            c.ProposalId,
            c.Action,
            c.Action == JitenFranchiseNodeAction.LinkExistingSeries ? c.MediaSeriesId : null)).ToList();

        try
        {
            var approved = topologyService.Approve(ApplyReviewId, ApplyExpectedFingerprint, domainChoices);
            var receipt = await topologyService.ApplyAsync(
                ApplyOperationId,
                id,
                ApplyReviewId,
                approved.ApprovalFingerprint!,
                cancellationToken);

            TempData["LibraryNotice"] = "Franchise topology changes applied successfully.";
            return RedirectToPage("/Franchises/Details", new
            {
                id,
                operationId = receipt.OperationId,
                replayed = false
            });
        }
        catch (JitenFranchiseTopologyReviewRequiredException ex)
        {
            if (ex.CanRetryCurrentReview)
            {
                var activeReview = topologyService.GetActiveReview(ApplyReviewId);
                if (activeReview is not null)
                {
                    await PopulateReviewViewModelAsync(activeReview, cancellationToken);
                    Error = ex.Message;
                    return Page();
                }
            }

            Error = $"{ex.Message} Please preview the Jiten graph again.";
            return Page();
        }
        catch (JitenHttpException ex)
        {
            Error = $"Jiten communication failed during apply: {ex.Message}. Review again.";
            return Page();
        }
        catch (OperationCanceledException)
        {
            Error = "The apply operation was cancelled.";
            return Page();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error applying franchise topology for {FranchiseId}", id);
            Error = "An unexpected error occurred while applying topology changes.";
            return Page();
        }
    }

    private async Task<bool> LoadDetailsAndAvailableAsync(Guid franchiseId, CancellationToken cancellationToken)
    {
        var details = await franchiseService.GetDetailsAsync(franchiseId, 100, cancellationToken);
        if (details is null)
        {
            return false;
        }

        var seriesList = details.Series.Select(s => new FranchiseSeriesItemViewModel(
            s.SeriesId,
            s.Title,
            s.MediaType,
            s.JitenDeckId,
            s.ProgressUnit,
            s.BookProgress,
            s.LifetimeCharactersRead,
            s.LifetimeMinutes)).ToList();

        FranchiseDetails = new FranchiseDetailsViewModel(
            details.Id,
            details.Title,
            details.JitenAnchorDeckId,
            seriesList,
            details.IsTruncated);

        var available = await context.MediaSeries.AsNoTracking()
            .Where(s => s.FranchiseId != franchiseId)
            .Include(s => s.Franchise)
            .OrderBy(s => s.MediaType)
            .ThenBy(s => s.Title)
            .Take(50)
            .Select(s => new AvailableSeriesOption(
                s.Id,
                s.Title,
                s.MediaType,
                s.Franchise != null ? s.Franchise.Title : null))
            .ToListAsync(cancellationToken);

        AvailableSeries = available;
        return true;
    }

    private async Task PopulateReviewViewModelAsync(
        JitenFranchiseTopologyReview review,
        CancellationToken cancellationToken)
    {
        var seriesIdsToLookup = review.Proposals
            .SelectMany(p => p.CandidateSeriesIds.Concat(p.ExactSeriesId.HasValue ? [p.ExactSeriesId.Value] : []))
            .Distinct()
            .ToList();

        var seriesTitles = await context.MediaSeries.AsNoTracking()
            .Where(s => seriesIdsToLookup.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Title, cancellationToken);

        var proposalVms = new List<FranchiseTopologyProposalViewModel>(review.Proposals.Count);
        Choices = [];

        foreach (var p in review.Proposals)
        {
            string? exactTitle = p.ExactSeriesId.HasValue && seriesTitles.TryGetValue(p.ExactSeriesId.Value, out var et)
                ? et
                : null;

            var candidates = p.CandidateSeriesIds
                .Select(cid => new CandidateSeriesOption(cid, seriesTitles.TryGetValue(cid, out var ct) ? ct : cid.ToString()[..8]))
                .ToList();

            var initialAction = p.RecommendedAction;
            Guid? initialSeriesId = p.ExactSeriesId ?? (p.CandidateSeriesIds.Count > 0 ? p.CandidateSeriesIds[0] : null);

            proposalVms.Add(new FranchiseTopologyProposalViewModel(
                p.ProposalId,
                p.Node.DeckId,
                p.Node.Title,
                p.Node.ProviderMediaType,
                p.Node.Classification,
                p.Node.CharacterCount,
                p.Node.ChildrenDeckCount,
                p.Kind,
                p.ExactSeriesId,
                exactTitle,
                candidates,
                p.AllowedActions,
                p.RecommendedAction,
                p.PersistedResolution,
                p.Message,
                initialAction,
                initialSeriesId));

            Choices.Add(new FranchiseTopologyChoiceInput
            {
                ProposalId = p.ProposalId,
                Action = initialAction,
                MediaSeriesId = initialSeriesId
            });
        }

        TopologyReview = new FranchiseTopologyReviewViewModel(
            review.ReviewId,
            review.FranchiseId,
            review.AnchorDeckId,
            review.Fingerprint,
            review.IsCompleteGraph,
            review.IsTruncated,
            review.Warning,
            review.CreatedAtUtc,
            review.ExpiresAtUtc,
            review.Nodes.Count,
            review.Edges.Count,
            proposalVms,
            Guid.NewGuid());
    }
}

