using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public class IndexModel : PageModel
{
    private readonly ISeriesService _seriesService;

    public IndexModel(ISeriesService seriesService)
    {
        _seriesService = seriesService ?? throw new ArgumentNullException(nameof(seriesService));
    }

    public IReadOnlyList<SeriesListItemViewModel> Series { get; private set; } = [];
    public int TotalCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public MediaType? Type { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken = default)
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

        if (Type == null && Search == null)
        {
            var seriesList = await _seriesService.GetAllSeriesAsync(null, null, cancellationToken);
            TotalCount = seriesList.Count;
            Series = seriesList.Select(SeriesListItemViewModel.FromEntity).ToList();
        }
        else
        {
            var allSeries = await _seriesService.GetAllSeriesAsync(null, null, cancellationToken);
            TotalCount = allSeries.Count;
            var filtered = await _seriesService.GetAllSeriesAsync(Type, Search, cancellationToken);
            Series = filtered.Select(SeriesListItemViewModel.FromEntity).ToList();
        }
    }
}
