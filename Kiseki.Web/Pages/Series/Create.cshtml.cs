using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public class CreateModel : PageModel
{
    private readonly ISeriesService _seriesService;
    private readonly IJitenApiClient _jitenApiClient;

    public CreateModel(
        ISeriesService seriesService,
        IJitenApiClient jitenApiClient)
    {
        _seriesService = seriesService ?? throw new ArgumentNullException(nameof(seriesService));
        _jitenApiClient = jitenApiClient ?? throw new ArgumentNullException(nameof(jitenApiClient));
    }

    [BindProperty(SupportsGet = true)]
    public string? Query { get; set; }

    public IReadOnlyList<JitenMediaSelection> Results { get; private set; } = [];

    [BindProperty]
    public ManualSeriesInput ManualInput { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(Query))
        {
            await SearchJitenAsync(cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostJitenAsync(
        int deckId,
        CancellationToken cancellationToken = default)
    {
        if (deckId <= 0)
        {
            ModelState.AddModelError(string.Empty, "Invalid Jiten deck ID.");
            return await ReloadWithResultsAsync(cancellationToken);
        }

        try
        {
            var series = await _seriesService.CreateSeriesFromJitenDeckAsync(deckId, cancellationToken);
            TempData["SeriesNotice"] = $"Created series '{series.Title}' with {series.Installments.Count} volumes.";
            return RedirectToPage("/Series/Details", new { id = series.Id });
        }
        catch (Exception exception) when (IsDisplayableJitenFailure(exception, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, JitenFailureMessage(exception));
            return await ReloadWithResultsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return await ReloadWithResultsAsync(cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostManualAsync(CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var series = await _seriesService.CreateManualSeriesAsync(
                ManualInput.Title,
                ManualInput.MediaType,
                cancellationToken);

            TempData["SeriesNotice"] = $"Created series '{series.Title}'.";
            return RedirectToPage("/Series/Details", new { id = series.Id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(nameof(ManualInput.Title), ex.Message);
            return Page();
        }
    }

    private async Task SearchJitenAsync(CancellationToken cancellationToken)
    {
        Query = Query?.Trim();
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        try
        {
            var decks = await _jitenApiClient.SearchBooksAsync(Query, cancellationToken);
            Results = decks.Select(JitenMediaSelection.FromDeck).ToList();

            if (Results.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "Jiten did not return any matching books.");
            }
        }
        catch (Exception exception) when (IsDisplayableJitenFailure(exception, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, JitenFailureMessage(exception));
        }
    }

    private async Task<IActionResult> ReloadWithResultsAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Query))
        {
            await SearchJitenAsync(cancellationToken);
        }

        return Page();
    }

    private static bool IsDisplayableJitenFailure(
        Exception exception,
        CancellationToken requestCancellationToken)
    {
        return exception is HttpRequestException or JsonException or NotSupportedException ||
               exception is OperationCanceledException && !requestCancellationToken.IsCancellationRequested;
    }

    private static string JitenFailureMessage(Exception exception)
    {
        return exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                "Jiten's request limit was reached. Wait a moment and try again.",
            OperationCanceledException => "Jiten took too long to respond. Try again.",
            JsonException or NotSupportedException =>
                "Jiten returned metadata that Kiseki could not read.",
            _ => "Kiseki could not reach Jiten. Check your connection and try again."
        };
    }

    public sealed class ManualSeriesInput
    {
        [Required(ErrorMessage = "Title is required.")]
        [Display(Name = "Series title")]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Media type")]
        public MediaType MediaType { get; set; } = MediaType.Book;
    }
}

