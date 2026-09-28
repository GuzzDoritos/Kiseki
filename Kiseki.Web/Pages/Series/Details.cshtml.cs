using System.ComponentModel.DataAnnotations;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public class DetailsModel : PageModel
{
    private readonly ISeriesService _seriesService;
    private readonly IReadingPaceService _paceService;

    public DetailsModel(
        ISeriesService seriesService,
        IReadingPaceService paceService)
    {
        _seriesService = seriesService ?? throw new ArgumentNullException(nameof(seriesService));
        _paceService = paceService ?? throw new ArgumentNullException(nameof(paceService));
    }

    public SeriesDetailsViewModel Series { get; private set; } = null!;

    [BindProperty]
    public AddInstallmentInput NewInstallment { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!await LoadSeriesAsync(id, cancellationToken))
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostLinkWorkAsync(
        Guid id,
        Guid installmentId,
        Guid mediaWorkId,
        CancellationToken cancellationToken = default)
    {
        if (installmentId == Guid.Empty || mediaWorkId == Guid.Empty)
        {
            return BadRequest();
        }

        try
        {
            await _seriesService.LinkWorkToInstallmentAsync(installmentId, mediaWorkId, cancellationToken);
            TempData["SeriesNotice"] = "Linked library book to volume.";
        }
        catch (Exception ex)
        {
            TempData["SeriesNotice"] = $"Error linking book: {ex.Message}";
        }

        return RedirectToPage("/Series/Details", new { id });
    }

    public async Task<IActionResult> OnPostUnlinkWorkAsync(
        Guid id,
        Guid installmentId,
        CancellationToken cancellationToken = default)
    {
        if (installmentId == Guid.Empty)
        {
            return BadRequest();
        }

        try
        {
            await _seriesService.UnlinkWorkFromInstallmentAsync(installmentId, cancellationToken);
            TempData["SeriesNotice"] = "Unlinked volume from library book.";
        }
        catch (Exception ex)
        {
            TempData["SeriesNotice"] = $"Error unlinking book: {ex.Message}";
        }

        return RedirectToPage("/Series/Details", new { id });
    }

    public async Task<IActionResult> OnPostAddInstallmentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            if (!await LoadSeriesAsync(id, cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        try
        {
            var added = await _seriesService.AddInstallmentAsync(
                id,
                NewInstallment.Title,
                NewInstallment.CharacterCount,
                NewInstallment.JitenSubdeckId,
                NewInstallment.CoverUrl,
                cancellationToken);

            TempData["SeriesNotice"] = $"Added volume '{added.Title}'.";
        }
        catch (Exception ex)
        {
            TempData["SeriesNotice"] = $"Error adding volume: {ex.Message}";
        }

        return RedirectToPage("/Series/Details", new { id });
    }

    public async Task<IActionResult> OnPostRemoveInstallmentAsync(
        Guid id,
        Guid installmentId,
        CancellationToken cancellationToken = default)
    {
        if (installmentId == Guid.Empty)
        {
            return BadRequest();
        }

        try
        {
            await _seriesService.RemoveInstallmentAsync(installmentId, cancellationToken);
            TempData["SeriesNotice"] = "Removed volume from series.";
        }
        catch (Exception ex)
        {
            TempData["SeriesNotice"] = $"Error removing volume: {ex.Message}";
        }

        return RedirectToPage("/Series/Details", new { id });
    }

    private async Task<bool> LoadSeriesAsync(Guid id, CancellationToken cancellationToken)
    {
        var series = await _seriesService.GetSeriesDetailsAsync(id, cancellationToken);
        if (series == null)
        {
            return false;
        }

        var available = await _seriesService.GetAvailableLibraryWorksAsync(id, cancellationToken);
        var pace = await _paceService.GetPaceProfileAsync(cancellationToken: cancellationToken);
        Series = SeriesDetailsViewModel.Create(series, available, pace, _paceService);
        return true;
    }

    public sealed class AddInstallmentInput
    {
        [Required(ErrorMessage = "Title is required.")]
        [Display(Name = "Volume title")]
        public string Title { get; set; } = string.Empty;

        [Range(0, int.MaxValue, ErrorMessage = "Character count cannot be negative.")]
        [Display(Name = "Character count")]
        public int CharacterCount { get; set; }

        [Display(Name = "Jiten subdeck ID (optional)")]
        public int? JitenSubdeckId { get; set; }

        [Display(Name = "Cover URL (optional)")]
        public string? CoverUrl { get; set; }
    }
}

