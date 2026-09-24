using System.ComponentModel.DataAnnotations;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Franchises;

public sealed class CreateModel(FranchiseCatalogueService service) : PageModel
{
    [BindProperty]
    public CreateFranchiseInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var franchise = await service.CreateAsync(
                new CreateFranchiseCommand(Input.Title, Input.JitenAnchorDeckId),
                cancellationToken);

            TempData["LibraryNotice"] = $"Franchise '{franchise.Title}' created.";
            return RedirectToPage("/Franchises/Details", new { id = franchise.Id });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            ModelState.AddModelError(nameof(Input.JitenAnchorDeckId), ex.Message);
            return Page();
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(Input.Title), ex.Message);
            return Page();
        }
        catch (MediaCatalogConflictException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public sealed class CreateFranchiseInput
    {
        [Required(ErrorMessage = "Title is required.")]
        public string Title { get; set; } = string.Empty;

        [Range(1, int.MaxValue, ErrorMessage = "Jiten anchor deck ID must be a positive number.")]
        public int? JitenAnchorDeckId { get; set; }
    }
}

