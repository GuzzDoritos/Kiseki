using System.ComponentModel.DataAnnotations;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public sealed class CreateModel(ImmersionDbContext context) : PageModel
{
    [BindProperty]
    public SeriesInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Input.Title))
        {
            ModelState.AddModelError("Input.Title", "Title is required.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            var series = await service.CreateSeriesAsync(
                new CreateSeriesCommand(Input.Title.Trim(), Input.MediaType),
                cancellationToken);

            TempData["LibraryNotice"] = $"Series '{series.Title}' created successfully.";
            return RedirectToPage("/Series/Details", new { id = series.Id });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public sealed class SeriesInput
    {
        [Required(ErrorMessage = "Title is required.")]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Media type")]
        [EnumDataType(typeof(MediaType), ErrorMessage = "Choose a valid media type.")]
        public MediaType MediaType { get; set; } = MediaType.Book;
    }
}
