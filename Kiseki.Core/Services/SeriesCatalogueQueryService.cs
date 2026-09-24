using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

/// <summary>Bounded, immutable, no-tracking read models for the manual series UI.</summary>
public sealed class SeriesCatalogueQueryService(ImmersionDbContext context)
{
    public async Task<SeriesCatalogueIndexResult> GetIndexAsync(
        SeriesCatalogueQueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SeriesCatalogueQueryOptions();
        var take = ValidateTake(options.SeriesTake, 1, 100, nameof(options.SeriesTake));
        var seriesRows = await context.MediaSeries.AsNoTracking()
            .Where(series => options.MediaType == null || series.MediaType == options.MediaType)
            .OrderBy(series => series.MediaType).ThenBy(series => series.Title).ThenBy(series => series.Id)
            .Take(take + 1)
            .Select(series => new SeriesRow(series.Id, series.Title, series.MediaType))
            .ToListAsync(cancellationToken);
        var isTruncated = seriesRows.Count > take;
        var series = seriesRows.Take(take).ToList();
        var progress = await ReadProgressAsync(series.Select(item => item.Id).ToList(), cancellationToken);
        return new SeriesCatalogueIndexResult(
            series.Select(item => new SeriesCatalogueIndexItem(item.Id, item.Title, item.MediaType,
                progress.TryGetValue(item.Id, out var value) ? value.Progress : EmptyProgress(item.Id, item.MediaType),
                progress.TryGetValue(item.Id, out value) ? value.Cover : null)).ToList(),
            isTruncated);
    }

    public async Task<SeriesCatalogueDetails?> GetDetailsAsync(
        Guid seriesId,
        int installmentTake = 500,
        CancellationToken cancellationToken = default)
    {
        installmentTake = ValidateTake(installmentTake, 1, 500, nameof(installmentTake));
        var series = await context.MediaSeries.AsNoTracking()
            .Where(item => item.Id == seriesId)
            .Select(item => new SeriesRow(item.Id, item.Title, item.MediaType))
            .SingleOrDefaultAsync(cancellationToken);
        if (series is null)
        {
            return null;
        }

        var installmentRows = await context.MediaInstallments.AsNoTracking()
            .Where(item => item.MediaSeriesId == seriesId)
            .OrderBy(item => item.OrderKey).ThenBy(item => item.Id)
            .Take(installmentTake + 1)
            .Select(item => new InstallmentRow(item.Id, item.MediaSeriesId, item.MediaType,
                item.TitleOverride ?? item.CanonicalTitle ?? item.LegacyTitle ?? string.Empty,
                item.OrderKey, item.Kind, item.IsIncluded, item.ReleaseStateOverride ?? item.ReleaseState,
                item.ReleaseDateOverride ?? item.ReleaseDate, item.CharacterCountOverride ?? item.CanonicalCharacterCount, item.Version,
                item.CanonicalCoverUrl, item.CanonicalCoverSource))
            .ToListAsync(cancellationToken);
        var isTruncated = installmentRows.Count > installmentTake;
        var includedRows = installmentRows.Take(installmentTake).ToList();
        var copyRows = await ReadCopyRowsAsync(includedRows, cancellationToken);
        // The displayed rows are deliberately bounded, but the headline always
        // describes the complete known catalogue for this series.
        var progressBySeries = await ReadProgressAsync([series.Id], cancellationToken);
        var progress = progressBySeries.TryGetValue(series.Id, out var seriesProgress)
            ? seriesProgress.Progress
            : EmptyProgress(series.Id, series.MediaType);
        var copiesByInstallment = copyRows.GroupBy(item => item.InstallmentId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Title).ThenBy(item => item.Id).ToList());

        return new SeriesCatalogueDetails(
            series.Id,
            series.Title,
            series.MediaType,
            progress,
            includedRows.Select(item => new SeriesCatalogueInstallmentItem(
                item.Id, item.DisplayTitle, item.OrderKey, item.Kind, item.IsIncluded,
                item.EffectiveReleaseState, item.EffectiveReleaseDate, item.CanonicalCharacterCount,
                item.Version, ResolveInstallmentCover(item, copyRows),
                copiesByInstallment.TryGetValue(item.Id, out var copies)
                    ? copies.Select(copy => new SeriesCatalogueCopyItem(copy.Id, copy.Title, copy.IsCompleted,
                        copy.EffectiveCharacterCount, copy.CharactersRead, copy.MinutesRead, copy.PositionProgressFraction,
                        ResolveCopyCover(copy))).ToList()
                    : [])).ToList(),
            isTruncated);
    }

    private async Task<Dictionary<Guid, SeriesReadResult>> ReadProgressAsync(
        IReadOnlyList<Guid> seriesIds,
        CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        var installments = await context.MediaInstallments.AsNoTracking()
            .Where(item => item.MediaSeriesId.HasValue && seriesIds.Contains(item.MediaSeriesId.Value))
            .OrderBy(item => item.OrderKey).ThenBy(item => item.Id)
            .Select(item => new InstallmentRow(item.Id, item.MediaSeriesId, item.MediaType,
                item.TitleOverride ?? item.CanonicalTitle ?? item.LegacyTitle ?? string.Empty,
                item.OrderKey, item.Kind, item.IsIncluded, item.ReleaseStateOverride ?? item.ReleaseState,
                item.ReleaseDateOverride ?? item.ReleaseDate, item.CharacterCountOverride ?? item.CanonicalCharacterCount, item.Version,
                item.CanonicalCoverUrl, item.CanonicalCoverSource))
            .ToListAsync(cancellationToken);
        var copies = await ReadCopyRowsAsync(installments, cancellationToken);
        return installments.GroupBy(item => item.MediaSeriesId!.Value)
            .ToDictionary(group => group.Key, group =>
            {
                var mediaType = group.First().MediaType;
                var groupRows = group.ToList();
                return new SeriesReadResult(
                    SeriesProgressCalculator.Calculate(new SeriesProgressInput(group.Key, mediaType,
                        BuildProgressInputs(groupRows, copies))),
                    groupRows.Select(row => ResolveInstallmentCover(row, copies)).FirstOrDefault(cover => cover is not null));
            });
    }

    private async Task<List<CopyRow>> ReadCopyRowsAsync(
        IReadOnlyList<InstallmentRow> installments,
        CancellationToken cancellationToken)
    {
        if (installments.Count == 0)
        {
            return [];
        }

        var installmentIds = installments.Select(item => item.Id).ToList();
        var canonicalCounts = installments.ToDictionary(item => item.Id, item => item.CanonicalCharacterCount);
        var providerKeys = await context.InstallmentProviderIdentities.AsNoTracking()
            .Where(identity => identity.Provider == "jiten" && installmentIds.Contains(identity.MediaInstallmentId))
            .Select(identity => new { identity.MediaInstallmentId, identity.NormalizedKey })
            .ToListAsync(cancellationToken);
        var identityKeys = providerKeys.GroupBy(item => item.MediaInstallmentId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.NormalizedKey).ToHashSet(StringComparer.Ordinal));
        var works = await context.MediaWorks.AsNoTracking()
            .Where(work => work.MediaInstallmentId.HasValue && installmentIds.Contains(work.MediaInstallmentId.Value))
            .Select(work => new WorkRow(work.Id, work.MediaInstallmentId!.Value, work.Title, work.IsCompleted,
                work.ManualCharacterCountOverride, work.TtsuCharacterCount, work.JitenCharacterCount,
                work.JitenDeckId, work.JitenSubdeckId, work.CoverUrl, work.CoverSource))
            .ToListAsync(cancellationToken);
        var workIds = works.Select(item => item.Id).ToList();
        var activity = await context.ImmersionLogs.AsNoTracking()
            .Where(log => log.MediaWorkId.HasValue && workIds.Contains(log.MediaWorkId.Value))
            .GroupBy(log => log.MediaWorkId!.Value)
            .Select(group => new { WorkId = group.Key, Characters = group.Sum(log => (long)log.CharactersRead), Minutes = group.Sum(log => log.TimeSpentMinutes) })
            .ToDictionaryAsync(item => item.WorkId, cancellationToken);
        var positions = await context.TtsuBindings.AsNoTracking()
            .Where(binding => workIds.Contains(binding.MediaWorkId))
            .Select(binding => new { binding.MediaWorkId, binding.ProgressFraction })
            .ToDictionaryAsync(item => item.MediaWorkId, item => item.ProgressFraction, cancellationToken);

        return works.Select(work =>
        {
            activity.TryGetValue(work.Id, out var totals);
            positions.TryGetValue(work.Id, out var position);
            identityKeys.TryGetValue(work.InstallmentId, out var keys);
            return new CopyRow(work.Id, work.InstallmentId, work.Title, work.IsCompleted,
                ResolveEffectiveCount(work, canonicalCounts[work.InstallmentId], keys),
                totals?.Characters ?? 0L, totals?.Minutes ?? 0d, position, work.CoverUrl, work.CoverSource);
        }).ToList();
    }

    private static IReadOnlyList<InstallmentProgressInput> BuildProgressInputs(
        IReadOnlyList<InstallmentRow> installments,
        IReadOnlyList<CopyRow> copies) => installments.Select(installment => new InstallmentProgressInput(
            installment.Id, installment.IsIncluded, installment.EffectiveReleaseState, installment.CanonicalCharacterCount,
            copies.Where(copy => copy.InstallmentId == installment.Id)
                .Select(copy => new CopyProgressInput(copy.Id, copy.IsCompleted, copy.EffectiveCharacterCount,
                    copy.CharactersRead, copy.MinutesRead, copy.PositionProgressFraction)).ToList())).ToList();

    private static int? ResolveEffectiveCount(WorkRow work, int? canonicalCount, HashSet<string>? identityKeys)
    {
        if (work.ManualCharacterCountOverride.HasValue)
        {
            return work.ManualCharacterCountOverride > 0 ? work.ManualCharacterCountOverride : null;
        }
        if (work.TtsuCharacterCount is > 0)
        {
            return work.TtsuCharacterCount;
        }
        if (canonicalCount is > 0 && identityKeys?.Contains(JitenKey(work)) == true)
        {
            return canonicalCount;
        }
        return work.JitenCharacterCount is > 0 ? work.JitenCharacterCount : null;
    }

    private static string JitenKey(WorkRow work) => work.JitenSubdeckId is int child
        ? $"subdeck:{work.JitenDeckId}:{child}"
        : $"deck:{work.JitenDeckId}";

    private static CatalogueCover? ResolveInstallmentCover(InstallmentRow installment, IReadOnlyList<CopyRow> copies)
    {
        if (!string.IsNullOrWhiteSpace(installment.CanonicalCoverUrl) &&
            installment.CanonicalCoverSource != CanonicalCoverSource.None)
        {
            return new CatalogueCover(installment.CanonicalCoverUrl, installment.CanonicalCoverSource switch
            {
                CanonicalCoverSource.ProviderExact => CatalogueCoverOrigin.CanonicalProviderExact,
                CanonicalCoverSource.ProviderParentFallback => CatalogueCoverOrigin.CanonicalProviderParentFallback,
                _ => CatalogueCoverOrigin.CanonicalLegacyUnknown
            });
        }

        var copy = copies.Where(item => item.InstallmentId == installment.Id && !string.IsNullOrWhiteSpace(item.CoverUrl))
            .OrderBy(item => item.Title).ThenBy(item => item.Id).FirstOrDefault();
        return copy is null ? null : new CatalogueCover(copy.CoverUrl!, CatalogueCoverOrigin.CopyLocal);
    }

    private static CatalogueCover? ResolveCopyCover(CopyRow copy) => string.IsNullOrWhiteSpace(copy.CoverUrl)
        ? null
        : new CatalogueCover(copy.CoverUrl, CatalogueCoverOrigin.CopyLocal);

    private static int ValidateTake(int value, int minimum, int maximum, string name) => value >= minimum && value <= maximum
        ? value : throw new ArgumentOutOfRangeException(name, $"Value must be between {minimum} and {maximum}.");

    private static SeriesProgressResult EmptyProgress(Guid id, MediaType mediaType) =>
        SeriesProgressCalculator.Calculate(new SeriesProgressInput(id, mediaType, []));

    private sealed record SeriesRow(Guid Id, string Title, MediaType MediaType);
    private sealed record InstallmentRow(Guid Id, Guid? MediaSeriesId, MediaType MediaType, string DisplayTitle,
        int OrderKey, InstallmentKind Kind, bool IsIncluded, ReleaseState EffectiveReleaseState,
        DateOnly? EffectiveReleaseDate, int? CanonicalCharacterCount, Guid Version,
        string? CanonicalCoverUrl, CanonicalCoverSource CanonicalCoverSource);
    private sealed record WorkRow(Guid Id, Guid InstallmentId, string Title, bool IsCompleted,
        int? ManualCharacterCountOverride, int? TtsuCharacterCount, int? JitenCharacterCount,
        int? JitenDeckId, int? JitenSubdeckId, string? CoverUrl, MediaCoverSource CoverSource);
    private sealed record CopyRow(Guid Id, Guid InstallmentId, string Title, bool IsCompleted,
        int? EffectiveCharacterCount, long CharactersRead, double MinutesRead, double? PositionProgressFraction,
        string? CoverUrl, MediaCoverSource CoverSource);
    private sealed record SeriesReadResult(SeriesProgressResult Progress, CatalogueCover? Cover);
}

public sealed record SeriesCatalogueQueryOptions(MediaType? MediaType = null, int SeriesTake = 100);
public sealed record SeriesCatalogueIndexResult(IReadOnlyList<SeriesCatalogueIndexItem> Items, bool IsTruncated);
public sealed record SeriesCatalogueIndexItem(Guid Id, string Title, MediaType MediaType,
    SeriesProgressResult Progress, CatalogueCover? Cover);
public sealed record SeriesCatalogueDetails(Guid Id, string Title, MediaType MediaType,
    SeriesProgressResult Progress, IReadOnlyList<SeriesCatalogueInstallmentItem> Installments, bool IsTruncated);
public sealed record SeriesCatalogueInstallmentItem(Guid Id, string Title, int OrderKey, InstallmentKind Kind,
    bool IsIncluded, ReleaseState EffectiveReleaseState, DateOnly? EffectiveReleaseDate,
    int? CanonicalCharacterCount, Guid Version, CatalogueCover? Cover,
    IReadOnlyList<SeriesCatalogueCopyItem> Copies);
public sealed record SeriesCatalogueCopyItem(Guid Id, string Title, bool IsCompleted, int? EffectiveCharacterCount,
    long CharactersRead, double MinutesRead, double? PositionProgressFraction, CatalogueCover? Cover);
public sealed record CatalogueCover(string Url, CatalogueCoverOrigin Origin);
public enum CatalogueCoverOrigin
{
    CanonicalProviderExact,
    CanonicalProviderParentFallback,
    CanonicalLegacyUnknown,
    CopyLocal
}
