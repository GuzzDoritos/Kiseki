using Kiseki.Core;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Library;

public sealed class DetailsModel(ImmersionDbContext dbContext) : PageModel
{
    public MediaWorkDetailsViewModel Work { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var work = await dbContext.MediaWorks
            .AsNoTracking()
            .Include(item => item.MediaSeries)
            .Include(item => item.Logs)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound();
        }

        Work = MediaWorkDetailsViewModel.Create(work);
        return Page();
    }
}
