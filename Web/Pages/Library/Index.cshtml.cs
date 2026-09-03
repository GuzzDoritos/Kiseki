using Microsoft.AspNetCore.Mvc.RazorPages;
using Web.Models;

namespace Web.Pages.Library;

public class IndexModel : PageModel
{
    public IReadOnlyList<MediaWorkListItemViewModel> Works { get; private set; } = [];

    public void OnGet()
    {
        // Database loading will be added when the first vertical slice is wired.
    }
}
