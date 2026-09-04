using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Kiseki.Web.Models;

namespace Kiseki.Web.Pages.Library;

public sealed class IndexModel(ImmersionDbContext dbContext) : PageModel
{
    public IReadOnlyList<MediaWorkListItemViewModel> Works { get; private set; } = [];
    public int TotalCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public MediaType? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public async Task OnGetAsync()
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        Status = Status?.Trim().ToLowerInvariant();
        if (Status is not ("in-progress" or "not-started" or "completed"))
        {
            Status = null;
        }

        TotalCount = await dbContext.MediaWorks.CountAsync();

        var query = dbContext.MediaWorks
            .AsNoTracking()
            .Include(work => work.MediaSeries)
            .Include(work => work.Logs)
            .AsQueryable();

        if (Search is not null)
        {
            query = query.Where(work => work.Title.Contains(Search));
        }

        if (Type is not null)
        {
            query = query.Where(work => work.MediaType == Type);
        }

        query = Status switch
        {
            "completed" => query.Where(work => work.IsCompleted),
            "in-progress" => query.Where(work => !work.IsCompleted && work.Logs.Any()),
            "not-started" => query.Where(work => !work.IsCompleted && !work.Logs.Any()),
            _ => query
        };

        var works = await query
            .OrderBy(work => work.IsCompleted)
            .ThenBy(work => work.Title)
            .ToListAsync();

        Works = works
            .Select(work => new MediaWorkListItemViewModel(
                work.Id,
                work.Title,
                work.MediaSeries?.Title,
                work.MediaType,
                work.CurrentCharactersRead,
                work.TotalCharacters,
                work.HasJitenLink,
                work.IsCompleted,
                work.Logs.Count))
            .ToList();
    }
}
