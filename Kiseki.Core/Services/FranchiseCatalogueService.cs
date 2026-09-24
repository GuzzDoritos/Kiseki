using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

/// <summary>
/// Core ownership boundary for manual franchise changes.  A franchise groups
/// independently tracked series; it never merges their catalogue units or
/// derives a cross-media progress value.
/// </summary>
public sealed class FranchiseCatalogueService(ImmersionDbContext context)
{
    public async Task<Franchise> CreateAsync(CreateFranchiseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var franchise = new Franchise(RequiredTitle(command.Title), command.JitenAnchorDeckId);
        context.Franchises.Add(franchise);
        await SaveAsync(cancellationToken);
        return franchise;
    }

    public async Task<Franchise> EditAsync(EditFranchiseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var franchise = await LoadFranchiseAsync(command.FranchiseId, cancellationToken);
        franchise.SetTitle(RequiredTitle(command.Title));
        franchise.SetJitenAnchorDeckId(command.JitenAnchorDeckId);
        await SaveAsync(cancellationToken);
        return franchise;
    }

    /// <summary>
    /// Moves a whole series to the requested franchise.  Its installments and
    /// copies stay with the series; no progress/history is rewritten.
    /// </summary>
    public async Task MoveSeriesAsync(MoveSeriesToFranchiseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var franchise = await LoadFranchiseAsync(command.FranchiseId, cancellationToken);
        var series = await LoadSeriesAsync(command.SeriesId, cancellationToken);
        series.Franchise = franchise;
        series.FranchiseId = franchise.Id;
        await SaveAsync(cancellationToken);
    }

    public async Task UnassignSeriesAsync(UnassignSeriesFromFranchiseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = await LoadFranchiseAsync(command.FranchiseId, cancellationToken);
        var series = await LoadSeriesAsync(command.SeriesId, cancellationToken);
        if (series.FranchiseId != command.FranchiseId)
        {
            throw new MediaCatalogConflictException("The selected series is no longer assigned to this franchise.");
        }

        series.Franchise = null;
        series.FranchiseId = null;
        await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes only the container.  Series, installments, copies, bindings,
    /// and activity remain and are explicitly unassigned before deletion.
    /// </summary>
    public async Task DeleteAsync(DeleteFranchiseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var franchise = await LoadFranchiseAsync(command.FranchiseId, cancellationToken);
        var assigned = await context.MediaSeries.Where(series => series.FranchiseId == franchise.Id)
            .ToListAsync(cancellationToken);
        foreach (var series in assigned)
        {
            series.Franchise = null;
            series.FranchiseId = null;
        }

        context.Franchises.Remove(franchise);
        await SaveAsync(cancellationToken);
    }

    public async Task<FranchiseCatalogueDetails?> GetDetailsAsync(Guid franchiseId, int seriesTake = 100,
        CancellationToken cancellationToken = default)
    {
        if (franchiseId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(franchiseId));
        if (seriesTake is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(seriesTake));

        var franchise = await context.Franchises.AsNoTracking()
            .Where(item => item.Id == franchiseId)
            .Select(item => new FranchiseRow(item.Id, item.Title, item.JitenAnchorDeckId))
            .SingleOrDefaultAsync(cancellationToken);
        if (franchise is null) return null;

        var rows = await context.MediaSeries.AsNoTracking()
            .Where(item => item.FranchiseId == franchiseId)
            .OrderBy(item => item.MediaType).ThenBy(item => item.Title).ThenBy(item => item.Id)
            .Take(seriesTake + 1)
            .Select(item => new SeriesRow(item.Id, item.Title, item.MediaType, item.JitenDeckId))
            .ToListAsync(cancellationToken);
        var isTruncated = rows.Count > seriesTake;
        var selected = rows.Take(seriesTake).ToArray();
        var seriesQuery = new SeriesCatalogueQueryService(context);
        var summaries = new List<FranchiseSeriesSummary>(selected.Length);
        foreach (var row in selected)
        {
            // This delegates the book-specific calculation to its existing
            // bounded, no-tracking read model.  Non-book units intentionally
            // have no invented percentage until their typed domains exist.
            var details = row.MediaType == MediaType.Book
                ? await seriesQuery.GetDetailsAsync(row.Id, cancellationToken: cancellationToken)
                : null;
            summaries.Add(new FranchiseSeriesSummary(
                row.Id,
                row.Title,
                row.MediaType,
                row.JitenDeckId,
                row.MediaType == MediaType.Book ? FranchiseProgressUnit.Characters : FranchiseProgressUnit.NotAvailable,
                details?.Progress,
                details?.Progress.LifetimeCharactersRead ?? 0,
                details?.Progress.LifetimeMinutes ?? 0));
        }

        return new FranchiseCatalogueDetails(franchise.Id, franchise.Title, franchise.JitenAnchorDeckId,
            summaries, isTruncated);
    }

    private async Task<Franchise> LoadFranchiseAsync(Guid id, CancellationToken cancellationToken) =>
        id != Guid.Empty
            ? await context.Franchises.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new MediaCatalogConflictException("The selected franchise no longer exists.")
            : throw new ArgumentOutOfRangeException(nameof(id));

    private async Task<MediaSeries> LoadSeriesAsync(Guid id, CancellationToken cancellationToken) =>
        id != Guid.Empty
            ? await context.MediaSeries.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new MediaCatalogConflictException("The selected series no longer exists.")
            : throw new ArgumentOutOfRangeException(nameof(id));

    private static string RequiredTitle(string title) => !string.IsNullOrWhiteSpace(title)
        ? title.Trim()
        : throw new ArgumentException("Title cannot be empty.", nameof(title));

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new MediaCatalogConflictException("The franchise changed. Refresh it before trying again.");
        }
    }

    private sealed record FranchiseRow(Guid Id, string Title, int? JitenAnchorDeckId);
    private sealed record SeriesRow(Guid Id, string Title, MediaType MediaType, int? JitenDeckId);
}

public sealed record CreateFranchiseCommand(string Title, int? JitenAnchorDeckId = null);
public sealed record EditFranchiseCommand(Guid FranchiseId, string Title, int? JitenAnchorDeckId = null);
public sealed record MoveSeriesToFranchiseCommand(Guid FranchiseId, Guid SeriesId);
public sealed record UnassignSeriesFromFranchiseCommand(Guid FranchiseId, Guid SeriesId);
public sealed record DeleteFranchiseCommand(Guid FranchiseId);

public sealed record FranchiseCatalogueDetails(Guid Id, string Title, int? JitenAnchorDeckId,
    IReadOnlyList<FranchiseSeriesSummary> Series, bool IsTruncated);

/// <summary>One row per series; no franchise-level percentage exists by design.</summary>
public sealed record FranchiseSeriesSummary(Guid SeriesId, string Title, MediaType MediaType, int? JitenDeckId,
    FranchiseProgressUnit ProgressUnit, SeriesProgressResult? BookProgress,
    long LifetimeCharactersRead, double LifetimeMinutes);

public enum FranchiseProgressUnit
{
    Characters,
    NotAvailable
}
