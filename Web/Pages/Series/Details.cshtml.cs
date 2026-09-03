using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages.Series;

public class DetailsModel : PageModel
{
    public Guid Id { get; private set; }

    public void OnGet(Guid id)
    {
        Id = id;
    }
}
