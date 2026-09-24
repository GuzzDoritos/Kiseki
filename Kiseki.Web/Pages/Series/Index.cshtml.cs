using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public sealed class IndexModel(ImmersionDbContext context) : PageModel
{
    public IReadOnlyList<SeriesIndexItemViewModel> Series { get; private set; } = [];
    public bool IsTruncated { get; private set; }

    [BindProperty(SupportsGet = true)]
    public MediaType? Type { get; set; }

    public string? Notice => TempData["LibraryNotice"] as string;
    public string? Error => TempData["LibraryError"] as string;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var queryService = new SeriesCatalogueQueryService(context);
        var result = await queryService.GetIndexAsync(new SeriesCatalogueQueryOptions(Type), cancellationToken);

        IsTruncated = result.IsTruncated;
        Series = result.Items
            .Select(item => new SeriesIndexItemViewModel(item.Id, item.Title, item.MediaType, item.Progress, item.Cover))
            .ToList();
    }
}
