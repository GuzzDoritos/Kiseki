using Kiseki.Core;
using Kiseki.Core.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Kiseki.Web.Models;

namespace Kiseki.Web.Pages.Library;

public sealed class IndexModel(ImmersionDbContext dbContext) : PageModel
{
    private const string ViewCookieName = "kiseki_library_view";

    public IReadOnlyList<MediaWorkListItemViewModel> Works { get; private set; } = [];
    public int TotalCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public MediaType? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? View { get; set; }

    public async Task OnGetAsync()
    {
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();
        Status = Status?.Trim().ToLowerInvariant();
        if (Status is not ("in-progress" or "not-started" or "completed"))
        {
            Status = null;
        }

        if (HttpContext is not null)
        {
            if (Request.Query.TryGetValue("View", out var requestedView) && !string.IsNullOrWhiteSpace(requestedView))
            {
                var normalized = requestedView.ToString().Trim().ToLowerInvariant();
                View = normalized is ("grid" or "list") ? normalized : "list";
                Response.Cookies.Append(ViewCookieName, View, new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true
                });
            }
            else if (Request.Cookies.TryGetValue(ViewCookieName, out var savedView) && !string.IsNullOrWhiteSpace(savedView))
            {
                var normalized = savedView.Trim().ToLowerInvariant();
                View = normalized is ("grid" or "list") ? normalized : "list";
            }
            else
            {
                View = "list";
            }
        }
        else
        {
            View = View is ("grid" or "list") ? View : "list";
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
            "in-progress" => query.Where(work => !work.IsCompleted && work.Logs.Any(log => log.CharactersRead > 0)),
            "not-started" => query.Where(work => !work.IsCompleted && !work.Logs.Any(log => log.CharactersRead > 0)),
            _ => query
        };

        var works = await query
            .OrderBy(work => work.IsCompleted)
            .ThenBy(work => work.Title)
            .ToListAsync();
        var workIds = works.Select(work => work.Id).ToList();
        var bindings = await dbContext.TtsuBindings.AsNoTracking()
            .Where(binding => workIds.Contains(binding.MediaWorkId))
            .ToDictionaryAsync(binding => binding.MediaWorkId);

        Works = works
            .Select(work =>
            {
                bindings.TryGetValue(work.Id, out var binding);
                return new MediaWorkListItemViewModel(
                    work.Id,
                    work.Title,
                    work.MediaSeries?.Title,
                    work.MediaType,
                    work.CurrentCharactersRead,
                    work.TotalCharacters,
                    work.CoverUrl,
                    work.HasJitenLink,
                    work.IsCompleted,
                    work.Logs.Count,
                    binding?.CurrentCharacterPosition,
                    binding?.ProgressFraction * 100d,
                    work.CoverSource,
                    work.CoverProviderItemId);
            })
            .ToList();
    }
}
