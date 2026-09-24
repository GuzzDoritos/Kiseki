using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Franchises;

public sealed class IndexModel(ImmersionDbContext context) : PageModel
{
    public IReadOnlyList<FranchiseIndexItemViewModel> Franchises { get; private set; } = [];

    public string? Notice => TempData["LibraryNotice"] as string;
    public string? Error => TempData["LibraryError"] as string;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var items = await context.Franchises.AsNoTracking()
            .OrderBy(f => f.Title)
            .Select(f => new
            {
                f.Id,
                f.Title,
                f.JitenAnchorDeckId,
                SeriesCount = f.Series.Count,
                BookSeriesCount = f.Series.Count(s => s.MediaType == MediaType.Book),
                AnimeSeriesCount = f.Series.Count(s => s.MediaType == MediaType.Anime),
                OtherSeriesCount = f.Series.Count(s => s.MediaType != MediaType.Book && s.MediaType != MediaType.Anime)
            })
            .ToListAsync(cancellationToken);

        Franchises = items.Select(f => new FranchiseIndexItemViewModel(
            f.Id,
            f.Title,
            f.JitenAnchorDeckId,
            f.SeriesCount,
            f.BookSeriesCount,
            f.AnimeSeriesCount,
            f.OtherSeriesCount)).ToList();
    }
}

