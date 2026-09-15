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

        var binding = await dbContext.TtsuBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MediaWorkId == id, cancellationToken);
        Work = MediaWorkDetailsViewModel.Create(work, binding);
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

        var binding = await dbContext.TtsuBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MediaWorkId == id, cancellationToken);
        work.ManualCharacterCountOverride = request?.ManualCharacterCount;
        if (work.TotalCharacters > 0)
        {
            work.IsCompleted = binding?.ProgressFraction is double positionProgress
                ? positionProgress >= 1
                : work.CurrentCharactersRead >= work.TotalCharacters;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var currentCharacters = work.CurrentCharactersRead;
        var totalCharacters = work.TotalCharacters;
        var progressLabel = ProgressLabel(work, binding);

        var characterTotalSource = work.ManualCharacterCountOverride.HasValue
            ? "Manual override"
            : work.TtsuCharacterCount.HasValue
                ? "TTSU progress"
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
            progressPercentage = ReadingProgressPercentage(work, binding),
            formattedProgressPercentage = ReadingProgressPercentage(work, binding).ToString("N1", System.Globalization.CultureInfo.InvariantCulture),
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

        var binding = await dbContext.TtsuBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MediaWorkId == id, cancellationToken);
        work.IsCompleted = !work.IsCompleted;
        await dbContext.SaveChangesAsync(cancellationToken);

        var currentCharacters = work.CurrentCharactersRead;
        var totalCharacters = work.TotalCharacters;
        var progressLabel = ProgressLabel(work, binding);

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
            progressPercentage = ReadingProgressPercentage(work, binding),
            formattedProgressPercentage = ReadingProgressPercentage(work, binding).ToString("N1", System.Globalization.CultureInfo.InvariantCulture),
            progressLabel = progressLabel
        });
    }

    public async Task<IActionResult> OnPostUpdateCoverUrlAsync(
        Guid id,
        [FromBody] UpdateCoverUrlRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.CoverUrl))
        {
            return BadRequest(new { message = "Cover image URL cannot be empty." });
        }

        var trimmedUrl = request.CoverUrl.Trim();
        if (trimmedUrl.Length > 2048)
        {
            return BadRequest(new { message = "Cover image URL cannot exceed 2,048 characters." });
        }

        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Cover image URL must be a valid HTTPS URL." });
        }

        var work = await dbContext.MediaWorks
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (work is null)
        {
            return NotFound(new { message = "Work not found." });
        }

        work.UpdateCoverUrl(trimmedUrl);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new JsonResult(new
        {
            success = true,
            coverUrl = work.JitenCoverUrl
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

    public sealed class UpdateCoverUrlRequest
    {
        public string? CoverUrl { get; set; }
    }

    private static double ReadingProgressPercentage(MediaWork work, TtsuBinding? binding) =>
        work.IsCompleted ? 100d : binding?.ProgressFraction is double fraction
            ? Math.Clamp(fraction * 100d, 0d, 100d)
            : work.ProgressPercentage;

    private static string ProgressLabel(MediaWork work, TtsuBinding? binding) =>
        binding?.CurrentCharacterPosition is int position && work.TotalCharacters > 0
            ? $"{position:N0} / {work.TotalCharacters:N0} character position"
            : work.TotalCharacters > 0
                ? $"{work.CurrentCharactersRead:N0} / {work.TotalCharacters:N0} characters"
                : $"{work.CurrentCharactersRead:N0} characters read";
}
