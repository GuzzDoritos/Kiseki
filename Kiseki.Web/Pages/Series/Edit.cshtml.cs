using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Series;

public class EditModel : PageModel
{
    public Guid Id { get; private set; }

    public void OnGet(Guid id)
    {
        Id = id;
    }
}
