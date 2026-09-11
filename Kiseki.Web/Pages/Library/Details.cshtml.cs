using Kiseki.Core;
using Kiseki.Core.Entities;
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

    public async Task<IActionResult> OnPostUpdateTitleAsync(
        Guid id,
        [FromBody] UpdateTitleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Title))
        {
            return BadRequest(new { message = "Title cannot be empty." });
        }

        var trimmedTitle = request.Title.Trim();
        if (trimmedTitle.Length > 500)
        {
            return BadRequest(new { message = "Title cannot exceed 500 characters." });
        }

        var work = await dbContext.MediaWorks
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound(new { message = "Work not found." });
        }

        work.Title = trimmedTitle;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new JsonResult(new
        {
            success = true,
            title = work.Title
        });
    }

    public async Task<IActionResult> OnPostUpdateCharacterTotalAsync(
        Guid id,
        [FromBody] UpdateCharacterTotalRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.ManualCharacterCount is int count && count < 0)
        {
            return BadRequest(new { message = "Character total cannot be negative." });
        }

        var work = await dbContext.MediaWorks
            .Include(item => item.Logs)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound(new { message = "Work not found." });
        }

        work.ManualCharacterCountOverride = request?.ManualCharacterCount;
        if (work.TotalCharacters > 0)
        {
            work.IsCompleted = work.CurrentCharactersRead >= work.TotalCharacters;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var currentCharacters = work.CurrentCharactersRead;
        var totalCharacters = work.TotalCharacters;
        var progressLabel = totalCharacters > 0
            ? $"{currentCharacters:N0} / {totalCharacters:N0} characters"
            : $"{currentCharacters:N0} characters read";

        var characterTotalSource = work.ManualCharacterCountOverride.HasValue
            ? "Manual override"
            : work.JitenCharacterCount.HasValue
                ? "Jiten"
                : "Not set";

        var statusLabel = work.IsCompleted
            ? "Completed"
            : currentCharacters > 0
                ? work.MediaType switch
                {
                    MediaType.Book => "Reading",
                    MediaType.Anime => "Watching",
                    MediaType.Game => "Playing",
                    _ => "In progress"
                }
                : "Not started";

        var statusCssClass = work.IsCompleted
            ? "completed"
            : currentCharacters > 0
                ? "active"
                : "idle";

        return new JsonResult(new
        {
            success = true,
            totalCharacters = totalCharacters,
            formattedTotalCharacters = totalCharacters > 0 ? totalCharacters.ToString("N0") : "Not set",
            manualOverride = work.ManualCharacterCountOverride,
            characterTotalSource = characterTotalSource,
            progressPercentage = work.ProgressPercentage,
            formattedProgressPercentage = work.ProgressPercentage.ToString("N1", System.Globalization.CultureInfo.InvariantCulture),
            progressLabel = progressLabel,
            isCompleted = work.IsCompleted,
            statusLabel = statusLabel,
            statusCssClass = statusCssClass
        });
    }

    public async Task<IActionResult> OnPostToggleStatusAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var work = await dbContext.MediaWorks
            .Include(item => item.Logs)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound(new { message = "Work not found." });
        }

        work.IsCompleted = !work.IsCompleted;
        await dbContext.SaveChangesAsync(cancellationToken);

        var currentCharacters = work.CurrentCharactersRead;
        var totalCharacters = work.TotalCharacters;
        var progressLabel = totalCharacters > 0
            ? $"{currentCharacters:N0} / {totalCharacters:N0} characters"
            : $"{currentCharacters:N0} characters read";

        var statusLabel = work.IsCompleted
            ? "Completed"
            : currentCharacters > 0
                ? work.MediaType switch
                {
                    MediaType.Book => "Reading",
                    MediaType.Anime => "Watching",
                    MediaType.Game => "Playing",
                    _ => "In progress"
                }
                : "Not started";

        var statusCssClass = work.IsCompleted
            ? "completed"
            : currentCharacters > 0
                ? "active"
                : "idle";

        return new JsonResult(new
        {
            success = true,
            isCompleted = work.IsCompleted,
            statusLabel = statusLabel,
            statusCssClass = statusCssClass,
            progressPercentage = work.ProgressPercentage,
            formattedProgressPercentage = work.ProgressPercentage.ToString("N1", System.Globalization.CultureInfo.InvariantCulture),
            progressLabel = progressLabel
        });
    }

    public sealed class UpdateTitleRequest
    {
        public string? Title { get; set; }
    }

    public sealed class UpdateCharacterTotalRequest
    {
        public int? ManualCharacterCount { get; set; }
    }
}
