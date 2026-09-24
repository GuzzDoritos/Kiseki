using System.ComponentModel.DataAnnotations;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public sealed class EditModel(ImmersionDbContext context) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public SeriesEditInput Input { get; set; } = new();

    public MediaType MediaType { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        var queryService = new SeriesCatalogueQueryService(context);
        var details = await queryService.GetDetailsAsync(id, cancellationToken: cancellationToken);

        if (details is null)
        {
            return NotFound();
        }

        Input.Title = details.Title;
        MediaType = details.MediaType;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;

        if (string.IsNullOrWhiteSpace(Input.Title))
        {
            ModelState.AddModelError("Input.Title", "Title is required.");
        }

        if (!ModelState.IsValid)
        {
            await ReloadSeriesAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            await service.EditSeriesAsync(new EditSeriesCommand(id, Input.Title.Trim()), cancellationToken);

            TempData["LibraryNotice"] = "Series updated successfully.";
            return RedirectToPage("/Series/Details", new { id });
        }
        catch (MediaCatalogConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await ReloadSeriesAsync(id, cancellationToken);
            return Page();
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await ReloadSeriesAsync(id, cancellationToken);
            return Page();
        }
    }

    private async Task ReloadSeriesAsync(Guid id, CancellationToken cancellationToken)
    {
        var queryService = new SeriesCatalogueQueryService(context);
        var details = await queryService.GetDetailsAsync(id, cancellationToken: cancellationToken);
        if (details is not null)
        {
            MediaType = details.MediaType;
        }
    }

    public sealed class SeriesEditInput
    {
        [Required(ErrorMessage = "Title is required.")]
        public string Title { get; set; } = string.Empty;
    }
}
