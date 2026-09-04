using Kiseki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Kiseki.Web.Pages.Series;

public class CreateModel : PageModel
{
    [BindProperty]
    public SeriesInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public sealed class SeriesInput
    {
        [Required]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Media type")]
        public MediaType MediaType { get; set; } = MediaType.Book;
    }
}
