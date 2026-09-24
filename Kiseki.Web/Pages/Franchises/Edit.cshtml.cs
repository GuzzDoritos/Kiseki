using System.ComponentModel.DataAnnotations;
using Kiseki.Core;
using Kiseki.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Franchises;

public sealed class EditModel(ImmersionDbContext context, FranchiseCatalogueService service) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public EditFranchiseInput Input { get; set; } = new();

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        var franchise = await context.Franchises.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (franchise is null)
        {
            return NotFound();
        }

        Input = new EditFranchiseInput
        {
            Title = franchise.Title,
            JitenAnchorDeckId = franchise.JitenAnchorDeckId
        };

        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await service.EditAsync(
                new EditFranchiseCommand(id, Input.Title, Input.JitenAnchorDeckId),
                cancellationToken);

            TempData["LibraryNotice"] = "Franchise updated.";
            return RedirectToPage("/Franchises/Details", new { id });
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

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        try
        {
            await service.DeleteAsync(new DeleteFranchiseCommand(id), cancellationToken);
            TempData["LibraryNotice"] = "Franchise deleted. Member series remain intact.";
            return RedirectToPage("/Franchises/Index");
        }
        catch (MediaCatalogConflictException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public sealed class EditFranchiseInput
    {
        [Required(ErrorMessage = "Title is required.")]
        public string Title { get; set; } = string.Empty;

        [Range(1, int.MaxValue, ErrorMessage = "Jiten anchor deck ID must be a positive number.")]
        public int? JitenAnchorDeckId { get; set; }
    }
}

