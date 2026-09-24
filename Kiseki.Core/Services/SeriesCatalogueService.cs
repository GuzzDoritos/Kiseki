using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

/// <summary>Transactional commands for the manual series catalogue.</summary>
public sealed class SeriesCatalogueService(ImmersionDbContext context)
{
    public async Task<MediaSeries> CreateSeriesAsync(CreateSeriesCommand command, CancellationToken cancellationToken = default)
    {
        ValidateMediaType(command.MediaType);
        var series = new MediaSeries(RequiredTitle(command.Title), command.MediaType);
        context.MediaSeries.Add(series);
        await SaveChangesAsync(cancellationToken);
        return series;
    }

    public async Task EditSeriesAsync(EditSeriesCommand command, CancellationToken cancellationToken = default)
    {
        var series = await FindSeriesAsync(command.SeriesId, cancellationToken);
        series.SetTitle(RequiredTitle(command.Title));
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<MediaInstallment> AddInstallmentAsync(AddInstallmentCommand command, CancellationToken cancellationToken = default)
    {
        var series = await FindSeriesAsync(command.SeriesId, cancellationToken);
        ValidateInstallmentKind(command.Kind);
        ValidateReleaseState(command.ReleaseState);
        var orderKey = command.OrderKey ?? await NextOrderKeyAsync(series.Id, cancellationToken);
        var installment = new MediaInstallment(RequiredTitle(command.Title), series.MediaType, orderKey)
        {
            MediaSeries = series,
            TitleOverride = RequiredTitle(command.Title),
            Kind = command.Kind,
            ReleaseState = command.ReleaseState,
            ReleaseDate = command.ReleaseDate,
            IsIncluded = command.IsIncluded,
            CharacterCountOverride = ValidCanonicalCount(command.CanonicalCharacterCount)
        };
        context.MediaInstallments.Add(installment);
        await SaveChangesAsync(cancellationToken);
        return installment;
    }

    public async Task EditInstallmentAsync(EditInstallmentCommand command, CancellationToken cancellationToken = default)
    {
        var installment = await FindInstallmentInSeriesAsync(command.SeriesId, command.InstallmentId, cancellationToken);
        CheckVersion(installment, command.ExpectedVersion);
        ValidateInstallmentKind(command.Kind);
        ValidateReleaseState(command.ReleaseStateOverride);
        installment.TitleOverride = RequiredTitle(command.Title);
        installment.Kind = command.Kind;
        installment.ReleaseStateOverride = command.ReleaseStateOverride;
        installment.ReleaseDateOverride = command.ReleaseDateOverride;
        installment.IsIncluded = command.IsIncluded;
        installment.CharacterCountOverride = ValidCanonicalCount(command.CanonicalCharacterCount);
        installment.Version = Guid.NewGuid();
        await SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderInstallmentsAsync(ReorderInstallmentsCommand command, CancellationToken cancellationToken = default)
    {
        var installments = await LoadOrderedInstallmentsAsync(command.SeriesId, cancellationToken);
        ValidateOrderSnapshot(installments, command.OrderedInstallmentIds, command.ExpectedVersions);
        ApplyOrder(installments.ToDictionary(item => item.Id), command.OrderedInstallmentIds);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task MoveInstallmentAsync(MoveInstallmentCommand command, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command.Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(command.Direction), "Choose a valid move direction.");
        }

        var installments = await LoadOrderedInstallmentsAsync(command.SeriesId, cancellationToken);
        var orderedIds = installments.Select(item => item.Id).ToList();
        ValidateOrderSnapshot(installments, orderedIds, command.ExpectedVersions);
        var index = orderedIds.IndexOf(command.InstallmentId);
        if (index < 0)
        {
            throw new MediaCatalogConflictException("The selected installment does not belong to this series.");
        }

        var targetIndex = command.Direction == InstallmentMoveDirection.Up ? index - 1 : index + 1;
        if (targetIndex < 0 || targetIndex >= orderedIds.Count)
        {
            return;
        }

        (orderedIds[index], orderedIds[targetIndex]) = (orderedIds[targetIndex], orderedIds[index]);
        ApplyOrder(installments.ToDictionary(item => item.Id), orderedIds);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<MediaWork> CreateCopyAsync(CreateCopyForInstallmentCommand command, CancellationToken cancellationToken = default)
    {
        var installment = await FindInstallmentInSeriesAsync(command.SeriesId, command.InstallmentId, cancellationToken);
        var copy = await new MediaCatalogService(context)
            .CreateTrackedCopyAsync(command.Title, installment.MediaType, installment.Id, cancellationToken: cancellationToken);
        await SaveChangesAsync(cancellationToken);
        return copy;
    }

    public async Task AssociateCopyAsync(AssociateCopyCommand command, CancellationToken cancellationToken = default)
    {
        var copy = await context.MediaWorks.SingleOrDefaultAsync(item => item.Id == command.CopyId, cancellationToken)
            ?? throw new MediaCatalogConflictException("The selected tracked copy no longer exists.");
        if (command.ExpectedCopyVersion == Guid.Empty || copy.Version != command.ExpectedCopyVersion ||
            copy.MediaInstallmentId != command.SourceInstallmentId)
        {
            throw new MediaCatalogConflictException("The selected copy changed. Refresh the series before associating it.");
        }

        var target = await FindInstallmentInSeriesAsync(command.SeriesId, command.InstallmentId, cancellationToken);
        CheckVersion(target, command.TargetExpectedVersion);
        MediaInstallment? source = null;
        if (command.SourceInstallmentId is Guid sourceId)
        {
            source = await context.MediaInstallments.SingleOrDefaultAsync(item => item.Id == sourceId, cancellationToken)
                ?? throw new MediaCatalogConflictException("The copy's source installment no longer exists.");
            CheckVersion(source, command.SourceExpectedVersion
                ?? throw new MediaCatalogConflictException("The source installment review is missing."));
            if (source.MediaSeriesId is not null)
            {
                throw new MediaCatalogConflictException(
                    "That copy already belongs to a series installment. Use reviewed consolidation to move it.");
            }
        }

        if (source?.Id == target.Id)
        {
            throw new MediaCatalogConflictException("That copy already belongs to the selected installment.");
        }

        if (source is not null)
        {
            await MoveClaimedProviderIdentitiesAsync(
                source, target, [copy], new HashSet<Guid> { copy.Id }, cancellationToken);
            source.Version = Guid.NewGuid();
        }
        await new MediaCatalogService(context).AssignCopyToInstallmentAsync(copy, command.InstallmentId, cancellationToken);
        target.Version = Guid.NewGuid();
        await SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a reviewed copy move only. Titles, provider identities, covers,
    /// logs, bindings, and source installment records are never merged/deleted.
    /// </summary>
    public async Task ConsolidateInstallmentsAsync(ReviewedCanonicalConsolidationCommand command, CancellationToken cancellationToken = default)
    {
        if (command.SourceInstallmentId == command.TargetInstallmentId || command.CopyIds.Count == 0)
        {
            throw new MediaCatalogConflictException("Choose distinct installments and at least one copy to consolidate.");
        }

        var source = await FindInstallmentInSeriesAsync(command.SeriesId, command.SourceInstallmentId, cancellationToken);
        var target = await FindInstallmentInSeriesAsync(command.SeriesId, command.TargetInstallmentId, cancellationToken);
        CheckVersion(source, command.SourceExpectedVersion);
        CheckVersion(target, command.TargetExpectedVersion);
        if (source.MediaType != target.MediaType)
        {
            throw new MediaCatalogConflictException("Only installments with the same media type can be consolidated.");
        }

        var copies = await context.MediaWorks
            .Where(copy => command.CopyIds.Contains(copy.Id) && copy.MediaInstallmentId == source.Id)
            .ToListAsync(cancellationToken);
        if (copies.Count != command.CopyIds.Distinct().Count())
        {
            throw new MediaCatalogConflictException("A reviewed copy changed installment before consolidation. Refresh the review.");
        }

        var movedCopyIds = copies.Select(copy => copy.Id).ToHashSet();
        await MoveClaimedProviderIdentitiesAsync(source, target, copies, movedCopyIds, cancellationToken);

        var catalog = new MediaCatalogService(context);
        foreach (var copy in copies)
        {
            await catalog.AssignCopyToInstallmentAsync(copy, target.Id, cancellationToken);
        }
        source.Version = Guid.NewGuid();
        target.Version = Guid.NewGuid();
        await SaveChangesAsync(cancellationToken);
    }

    private async Task<MediaSeries> FindSeriesAsync(Guid id, CancellationToken cancellationToken) =>
        await context.MediaSeries.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
        ?? throw new MediaCatalogConflictException("The selected series no longer exists.");

    private async Task<MediaInstallment> FindInstallmentInSeriesAsync(
        Guid seriesId,
        Guid installmentId,
        CancellationToken cancellationToken)
    {
        if (seriesId == Guid.Empty || installmentId == Guid.Empty)
        {
            throw new MediaCatalogConflictException("Choose a valid series and installment.");
        }

        return await context.MediaInstallments.SingleOrDefaultAsync(
            item => item.Id == installmentId && item.MediaSeriesId == seriesId,
            cancellationToken)
            ?? throw new MediaCatalogConflictException("The selected installment does not belong to this series.");
    }

    private async Task<int> NextOrderKeyAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var last = await context.MediaInstallments.Where(item => item.MediaSeriesId == seriesId)
            .Select(item => (int?)item.OrderKey).MaxAsync(cancellationToken) ?? 0;
        return checked(last + 100);
    }

    private Task<List<MediaInstallment>> LoadOrderedInstallmentsAsync(Guid seriesId, CancellationToken cancellationToken) =>
        context.MediaInstallments
            .Where(item => item.MediaSeriesId == seriesId)
            .OrderBy(item => item.OrderKey).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    private static void ValidateOrderSnapshot(
        IReadOnlyList<MediaInstallment> installments,
        IReadOnlyList<Guid> orderedIds,
        IReadOnlyDictionary<Guid, Guid> expectedVersions)
    {
        if (installments.Count != orderedIds.Count ||
            installments.Select(item => item.Id).Except(orderedIds).Any() ||
            orderedIds.Distinct().Count() != orderedIds.Count ||
            expectedVersions.Count != installments.Count ||
            installments.Any(item => !expectedVersions.TryGetValue(item.Id, out var version) || version != item.Version))
        {
            throw new MediaCatalogConflictException("The installment order changed. Refresh the series before reordering.");
        }
    }

    private static void ApplyOrder(
        IReadOnlyDictionary<Guid, MediaInstallment> installments,
        IReadOnlyList<Guid> orderedIds)
    {
        for (var index = 0; index < orderedIds.Count; index++)
        {
            var installment = installments[orderedIds[index]];
            installment.OrderKey = checked((index + 1) * 100);
            installment.Version = Guid.NewGuid();
        }
    }

    private async Task MoveClaimedProviderIdentitiesAsync(
        MediaInstallment source,
        MediaInstallment target,
        IReadOnlyList<MediaWork> copies,
        IReadOnlySet<Guid> movedCopyIds,
        CancellationToken cancellationToken)
    {
        var sourceIdentities = await context.InstallmentProviderIdentities
            .Where(identity => identity.MediaInstallmentId == source.Id)
            .ToListAsync(cancellationToken);
        foreach (var identity in sourceIdentities)
        {
            var matchingMovedCopies = copies.Where(copy =>
                identity.Provider == "jiten" && JitenKey(copy) == identity.NormalizedKey).ToList();
            if (matchingMovedCopies.Count == 0)
            {
                continue;
            }

            var retainedClaim = await context.MediaWorks.AnyAsync(copy =>
                copy.MediaInstallmentId == source.Id && !movedCopyIds.Contains(copy.Id) &&
                copy.JitenDeckId == matchingMovedCopies[0].JitenDeckId &&
                copy.JitenSubdeckId == matchingMovedCopies[0].JitenSubdeckId,
                cancellationToken);
            if (retainedClaim)
            {
                throw new MediaCatalogConflictException(
                    "Move every copy sharing the provider identity, or leave those copies on the source installment.");
            }

            identity.MediaInstallment = target;
            identity.MediaInstallmentId = target.Id;
        }
    }

    private static string RequiredTitle(string title) => !string.IsNullOrWhiteSpace(title)
        ? title.Trim()
        : throw new ArgumentException("Title cannot be empty.", nameof(title));

    private static int? ValidCanonicalCount(int? count) => count switch
    {
        null => null,
        > 0 => count,
        _ => throw new ArgumentOutOfRangeException(nameof(count), "Canonical character count must be positive or unknown.")
    };

    private static void CheckVersion(MediaInstallment installment, Guid expectedVersion)
    {
        if (expectedVersion == Guid.Empty || installment.Version != expectedVersion)
        {
            throw new MediaCatalogConflictException("This installment changed. Refresh it before applying your edits.");
        }
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new MediaCatalogConflictException("The catalogue changed. Refresh the series before trying again.");
        }
    }

    private static void ValidateMediaType(MediaType mediaType)
    {
        if (!Enum.IsDefined(mediaType))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType), "Choose a valid media type.");
        }
    }

    private static void ValidateInstallmentKind(InstallmentKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Choose a valid installment kind.");
        }
    }

    private static void ValidateReleaseState(ReleaseState? releaseState)
    {
        if (releaseState.HasValue && !Enum.IsDefined(releaseState.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(releaseState), "Choose a valid release state.");
        }
    }

    private static string? JitenKey(MediaWork copy) => copy.JitenDeckId is not int parent
        ? null
        : copy.JitenSubdeckId is int child
            ? $"subdeck:{parent}:{child}"
            : $"deck:{parent}";
}

public sealed record CreateSeriesCommand(string Title, MediaType MediaType);
public sealed record EditSeriesCommand(Guid SeriesId, string Title);
public sealed record AddInstallmentCommand(Guid SeriesId, string Title, InstallmentKind Kind,
    ReleaseState ReleaseState, DateOnly? ReleaseDate, bool IsIncluded = true,
    int? CanonicalCharacterCount = null, int? OrderKey = null);
public sealed record EditInstallmentCommand(Guid SeriesId, Guid InstallmentId, Guid ExpectedVersion, string Title,
    InstallmentKind Kind, ReleaseState? ReleaseStateOverride, DateOnly? ReleaseDateOverride,
    bool IsIncluded, int? CanonicalCharacterCount);
public sealed record ReorderInstallmentsCommand(Guid SeriesId, IReadOnlyList<Guid> OrderedInstallmentIds,
    IReadOnlyDictionary<Guid, Guid> ExpectedVersions);
public enum InstallmentMoveDirection { Up = 0, Down = 1 }
public sealed record MoveInstallmentCommand(Guid SeriesId, Guid InstallmentId, InstallmentMoveDirection Direction,
    IReadOnlyDictionary<Guid, Guid> ExpectedVersions);
public sealed record CreateCopyForInstallmentCommand(Guid SeriesId, Guid InstallmentId, string Title);
public sealed record AssociateCopyCommand(Guid SeriesId, Guid CopyId, Guid ExpectedCopyVersion,
    Guid? SourceInstallmentId, Guid? SourceExpectedVersion, Guid InstallmentId, Guid TargetExpectedVersion);
public sealed record ReviewedCanonicalConsolidationCommand(Guid SeriesId, Guid SourceInstallmentId, Guid SourceExpectedVersion,
    Guid TargetInstallmentId, Guid TargetExpectedVersion, IReadOnlyList<Guid> CopyIds);
