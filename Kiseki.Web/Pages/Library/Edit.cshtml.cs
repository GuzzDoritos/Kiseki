using Kiseki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace Kiseki.Web.Pages.Library;

public class EditModel : PageModel
{
    public Guid Id { get; private set; }

    [BindProperty]
    public EditWorkInput Input { get; set; } = new();

    public void OnGet(Guid id)
    {
        Id = id;
    }

    public sealed class EditWorkInput
    {
        [Required]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Media type")]
        public MediaType MediaType { get; set; } = MediaType.Book;

        [Display(Name = "Manual character count")]
        [Range(0, int.MaxValue)]
        public int? ManualCharacterCount { get; set; }
    }
}
