using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Library;

public class DetailsModel : PageModel
{
    public Guid Id { get; private set; }

    public void OnGet(Guid id)
    {
        Id = id;
    }
}
