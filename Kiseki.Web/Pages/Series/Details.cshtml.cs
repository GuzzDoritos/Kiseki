using System.ComponentModel.DataAnnotations;
using Kiseki.Core;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Series;

public sealed class DetailsModel(ImmersionDbContext context) : PageModel
{
    public SeriesDetailsViewModel SeriesDetails { get; private set; } = null!;

    public string? Notice => TempData["LibraryNotice"] as string;
    public string? Error => TempData["LibraryError"] as string;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var queryService = new SeriesCatalogueQueryService(context);
        var details = await queryService.GetDetailsAsync(id, cancellationToken: cancellationToken);

        if (details is null)
        {
            return NotFound();
        }

        var availableWorks = await context.MediaWorks.AsNoTracking()
            .Where(w => w.MediaType == details.MediaType &&
                (w.MediaInstallmentId == null || w.MediaInstallment!.MediaSeriesId == null))
            .OrderBy(w => w.Title)
            .Select(w => new AvailableCopyOption(
                w.Id,
                w.Title,
                w.Version,
                w.MediaInstallmentId,
                w.MediaInstallment == null ? null : w.MediaInstallment.Version))
            .ToListAsync(cancellationToken);

        var progressMap = details.Progress.Installments.ToDictionary(i => i.InstallmentId);

        var installmentViewModels = details.Installments.Select(inst =>
        {
            progressMap.TryGetValue(inst.Id, out var progressResult);
            var copies = inst.Copies.Select(c => new SeriesCopyViewModel(
                c.Id,
                c.Title,
                c.IsCompleted,
                c.EffectiveCharacterCount,
                c.CharactersRead,
                c.MinutesRead,
                c.PositionProgressFraction,
                c.Cover)).ToList();

            return new SeriesInstallmentViewModel(
                inst.Id,
                inst.Title,
                inst.OrderKey,
                inst.Kind,
                inst.IsIncluded,
                inst.EffectiveReleaseState,
                inst.EffectiveReleaseDate,
                inst.CanonicalCharacterCount,
                inst.Version,
                inst.Cover,
                progressResult,
                copies);
        }).ToList();

        SeriesDetails = new SeriesDetailsViewModel(
            details.Id,
            details.Title,
            details.MediaType,
            details.Progress,
            installmentViewModels,
            details.IsTruncated,
            availableWorks);

        return Page();
    }

    public async Task<IActionResult> OnPostAddInstallmentAsync(Guid id, AddInstallmentInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["LibraryError"] = "Correct the invalid installment values and try again.";
            return RedirectToPage(new { id });
        }
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            TempData["LibraryError"] = "Installment title is required.";
            return RedirectToPage(new { id });
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            var installment = await service.AddInstallmentAsync(new AddInstallmentCommand(
                id,
                input.Title.Trim(),
                input.Kind,
                input.ReleaseState,
                input.ReleaseDate,
                input.IsIncluded,
                input.CanonicalCharacterCount,
                input.OrderKey), cancellationToken);

            TempData["LibraryNotice"] = $"Installment '{installment.TitleOverride ?? installment.CanonicalTitle}' added successfully.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostEditInstallmentAsync(Guid id, EditInstallmentInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["LibraryError"] = "Correct the invalid installment values and try again.";
            return RedirectToPage(new { id });
        }
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            TempData["LibraryError"] = "Installment title is required.";
            return RedirectToPage(new { id });
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            await service.EditInstallmentAsync(new EditInstallmentCommand(
                id,
                input.InstallmentId,
                input.ExpectedVersion,
                input.Title.Trim(),
                input.Kind,
                input.ReleaseStateOverride,
                input.ReleaseDateOverride,
                input.IsIncluded,
                input.CanonicalCharacterCount), cancellationToken);

            TempData["LibraryNotice"] = "Installment updated successfully.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReorderInstallmentsAsync(Guid id, ReorderInstallmentsInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || input.ExpectedVersions is null || input.ExpectedVersions.Count == 0)
        {
            TempData["LibraryError"] = "The order snapshot is missing or invalid. Refresh the series and try again.";
            return RedirectToPage(new { id });
        }
        try
        {
            var service = new SeriesCatalogueService(context);
            if (input.OrderedInstallmentIds is { Count: > 0 })
            {
                await service.ReorderInstallmentsAsync(
                    new ReorderInstallmentsCommand(id, input.OrderedInstallmentIds, input.ExpectedVersions), cancellationToken);
            }
            else if (input.InstallmentId.HasValue &&
                     input.Direction is not null &&
                     (input.Direction.Equals("up", StringComparison.OrdinalIgnoreCase) ||
                      input.Direction.Equals("down", StringComparison.OrdinalIgnoreCase)))
            {
                var direction = input.Direction.Equals("up", StringComparison.OrdinalIgnoreCase)
                    ? InstallmentMoveDirection.Up
                    : InstallmentMoveDirection.Down;
                await service.MoveInstallmentAsync(
                    new MoveInstallmentCommand(id, input.InstallmentId.Value, direction, input.ExpectedVersions),
                    cancellationToken);
            }
            else
            {
                TempData["LibraryError"] = "Invalid reorder request.";
                return RedirectToPage(new { id });
            }

            TempData["LibraryNotice"] = "Installments reordered successfully.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCreateCopyAsync(Guid id, CreateCopyInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["LibraryError"] = "Correct the invalid copy values and try again.";
            return RedirectToPage(new { id });
        }
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            TempData["LibraryError"] = "Tracked copy title is required.";
            return RedirectToPage(new { id });
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            var copy = await service.CreateCopyAsync(
                new CreateCopyForInstallmentCommand(id, input.InstallmentId, input.Title.Trim()),
                cancellationToken);

            TempData["LibraryNotice"] = $"Tracked copy '{copy.Title}' created for installment.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAssociateCopyAsync(Guid id, AssociateCopyInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["LibraryError"] = "Choose a valid unassigned copy and installment.";
            return RedirectToPage(new { id });
        }
        if (!TryParseCopyReview(input.CopyReview, out var copyId, out var copyVersion,
                out var sourceInstallmentId, out var sourceInstallmentVersion))
        {
            TempData["LibraryError"] = "The copy review is invalid. Refresh the series and choose it again.";
            return RedirectToPage(new { id });
        }
        try
        {
            var service = new SeriesCatalogueService(context);
            await service.AssociateCopyAsync(
                new AssociateCopyCommand(id, copyId, copyVersion, sourceInstallmentId,
                    sourceInstallmentVersion, input.InstallmentId, input.TargetExpectedVersion),
                cancellationToken);

            TempData["LibraryNotice"] = "Tracked copy associated with installment.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    private static bool TryParseCopyReview(
        string? value,
        out Guid copyId,
        out Guid copyVersion,
        out Guid? sourceInstallmentId,
        out Guid? sourceInstallmentVersion)
    {
        copyId = Guid.Empty;
        copyVersion = Guid.Empty;
        sourceInstallmentId = null;
        sourceInstallmentVersion = null;
        var parts = value?.Split('|');
        if (parts is not { Length: 4 } ||
            !Guid.TryParse(parts[0], out copyId) || copyId == Guid.Empty ||
            !Guid.TryParse(parts[1], out copyVersion) || copyVersion == Guid.Empty)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(parts[2]))
        {
            if (!Guid.TryParse(parts[2], out var parsedSourceId) || parsedSourceId == Guid.Empty ||
                !Guid.TryParse(parts[3], out var parsedSourceVersion) || parsedSourceVersion == Guid.Empty)
            {
                return false;
            }
            sourceInstallmentId = parsedSourceId;
            sourceInstallmentVersion = parsedSourceVersion;
        }
        else if (!string.IsNullOrEmpty(parts[3]))
        {
            return false;
        }

        return true;
    }

    public async Task<IActionResult> OnPostConsolidateAsync(Guid id, ConsolidateInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["LibraryError"] = "The consolidation review is invalid. Refresh the series and try again.";
            return RedirectToPage(new { id });
        }
        if (input.CopyIds is null || input.CopyIds.Count == 0)
        {
            TempData["LibraryError"] = "Choose at least one copy to consolidate.";
            return RedirectToPage(new { id });
        }

        var targetId = input.TargetInstallmentId;
        var targetVersion = input.TargetExpectedVersion;

        if (!string.IsNullOrWhiteSpace(input.TargetCombined) && input.TargetCombined.Contains('|'))
        {
            var parts = input.TargetCombined.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var parsedId) && Guid.TryParse(parts[1], out var parsedVer))
            {
                targetId = parsedId;
                targetVersion = parsedVer;
            }
        }

        if (targetId == Guid.Empty || targetVersion == Guid.Empty)
        {
            TempData["LibraryError"] = "Please select a valid target installment.";
            return RedirectToPage(new { id });
        }

        try
        {
            var service = new SeriesCatalogueService(context);
            await service.ConsolidateInstallmentsAsync(new ReviewedCanonicalConsolidationCommand(
                id,
                input.SourceInstallmentId,
                input.SourceExpectedVersion,
                targetId,
                targetVersion,
                input.CopyIds), cancellationToken);

            TempData["LibraryNotice"] = "Installments consolidated successfully.";
        }
        catch (MediaCatalogConflictException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }
        catch (ArgumentException ex)
        {
            TempData["LibraryError"] = ex.Message;
        }

        return RedirectToPage(new { id });
    }

    public sealed class AddInstallmentInput
    {
        [Required]
        public string Title { get; set; } = string.Empty;
        [EnumDataType(typeof(InstallmentKind))]
        public InstallmentKind Kind { get; set; } = InstallmentKind.Volume;
        [EnumDataType(typeof(ReleaseState))]
        public ReleaseState ReleaseState { get; set; } = ReleaseState.Released;
        public DateOnly? ReleaseDate { get; set; }
        public bool IsIncluded { get; set; } = true;
        [Range(1, int.MaxValue)]
        public int? CanonicalCharacterCount { get; set; }
        [Range(0, int.MaxValue)]
        public int? OrderKey { get; set; }
    }

    public sealed class EditInstallmentInput
    {
        [Required]
        public Guid InstallmentId { get; set; }
        [Required]
        public Guid ExpectedVersion { get; set; }
        [Required]
        public string Title { get; set; } = string.Empty;
        [EnumDataType(typeof(InstallmentKind))]
        public InstallmentKind Kind { get; set; } = InstallmentKind.Volume;
        [EnumDataType(typeof(ReleaseState))]
        public ReleaseState? ReleaseStateOverride { get; set; }
        public DateOnly? ReleaseDateOverride { get; set; }
        public bool IsIncluded { get; set; } = true;
        [Range(1, int.MaxValue)]
        public int? CanonicalCharacterCount { get; set; }
    }

    public sealed class ReorderInstallmentsInput
    {
        public List<Guid>? OrderedInstallmentIds { get; set; }
        public Dictionary<Guid, Guid>? ExpectedVersions { get; set; }
        public Guid? InstallmentId { get; set; }
        public string? Direction { get; set; }
    }

    public sealed class CreateCopyInput
    {
        [Required]
        public Guid InstallmentId { get; set; }
        [Required]
        public string Title { get; set; } = string.Empty;
    }

    public sealed class AssociateCopyInput
    {
        [Required]
        public string CopyReview { get; set; } = string.Empty;
        [Required]
        public Guid InstallmentId { get; set; }
        [Required]
        public Guid TargetExpectedVersion { get; set; }
    }

    public sealed class ConsolidateInput
    {
        [Required]
        public Guid SourceInstallmentId { get; set; }
        [Required]
        public Guid SourceExpectedVersion { get; set; }
        public Guid TargetInstallmentId { get; set; }
        public Guid TargetExpectedVersion { get; set; }
        public string? TargetCombined { get; set; }
        public List<Guid> CopyIds { get; set; } = [];
    }
}
