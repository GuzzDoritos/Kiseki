using System.Globalization;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;

namespace Kiseki.Web.Models;

public sealed record SeriesIndexItemViewModel(
    Guid Id,
    string Title,
    MediaType MediaType,
    SeriesProgressResult Progress,
    CatalogueCover? Cover)
{
    public double? ProgressPercentage => Progress.ProgressPercentage;

    public int TotalInstallments => Progress.Installments.Count;
    public int ReleasedVolumeCount => Progress.ReleasedIncludedInstallmentCount;
    public int CompletedVolumeCount => Progress.CompletedReleasedIncludedInstallmentCount;
    public int TrackedVolumeCount => Progress.TrackedReleasedIncludedInstallmentCount;
    public int UntrackedVolumeCount => Progress.UntrackedReleasedIncludedInstallmentCount;
    public bool IsComplete => Progress.IsSeriesComplete;
    public int ComputableVolumeCount => Progress.ComputableProgressInstallmentCount;
    public bool HasPartialCoverage => ReleasedVolumeCount > 0 && ComputableVolumeCount < ReleasedVolumeCount;
    public string PartialCoverageLabel => HasPartialCoverage ? $"covers {ComputableVolumeCount} of {ReleasedVolumeCount} volumes" : string.Empty;

    public string CompletedVolumesSummary => ReleasedVolumeCount > 0
        ? $"{CompletedVolumeCount} / {ReleasedVolumeCount} completed"
        : TotalInstallments > 0
            ? "0 released volumes"
            : "No volumes";

    public string TrackingSummary => $"{TrackedVolumeCount} tracked, {UntrackedVolumeCount} not tracked";

    public string StatusLabel => IsComplete
        ? "Completed"
        : TrackedVolumeCount > 0 || CompletedVolumeCount > 0
            ? "In progress"
            : "Not started";

    public string StatusCss => IsComplete
        ? "media-status-completed"
        : TrackedVolumeCount > 0 || CompletedVolumeCount > 0
            ? "media-status-active"
            : "media-status-not-started";

    public ProgressBarViewModel ProgressBar => new(
        Current: Progress.ComputableProgressCharacterCount > 0 && Progress.ProgressPercentage.HasValue
            ? (int)Math.Round(Progress.ComputableProgressCharacterCount * (Progress.ProgressPercentage.Value / 100d))
            : 0,
        Total: (int)Math.Min(int.MaxValue, Progress.ComputableProgressCharacterCount),
        Label: Progress.ProgressPercentage.HasValue
            ? (HasPartialCoverage
                ? $"{Progress.ProgressPercentage.Value.ToString("0.0", CultureInfo.InvariantCulture)}% ({PartialCoverageLabel})"
                : $"{Progress.ProgressPercentage.Value.ToString("0.0", CultureInfo.InvariantCulture)}%")
            : "No character data",
        IsComplete: Progress.IsSeriesComplete,
        ShowPercentage: Progress.ProgressPercentage.HasValue,
        PercentageOverride: Progress.ProgressPercentage);

    public int EligibleCount => Progress.EligibleInstallmentCount;
    public int TrackedCount => Math.Max(0, Progress.EligibleInstallmentCount - Progress.UntrackedEligibleInstallmentCount);
    public int UntrackedCount => Progress.UntrackedEligibleInstallmentCount;
    public long LifetimeCharacters => Progress.LifetimeCharactersRead;
    public double LifetimeMinutes => Progress.LifetimeMinutes;

    public string TypeToJapanese => MediaType switch
    {
        MediaType.Book => "本",
        MediaType.Anime => "アニメ",
        MediaType.Game => "ゲーム",
        _ => "--"
    };
}

public sealed record SeriesDetailsViewModel(
    Guid Id,
    string Title,
    MediaType MediaType,
    SeriesProgressResult Progress,
    IReadOnlyList<SeriesInstallmentViewModel> Installments,
    bool IsTruncated,
    IReadOnlyList<AvailableCopyOption> AvailableCopies)
{
    public double? ProgressPercentage => Progress.ProgressPercentage;

    public int TotalInstallments => Progress.Installments.Count;
    public int ReleasedVolumeCount => Progress.ReleasedIncludedInstallmentCount;
    public int CompletedVolumeCount => Progress.CompletedReleasedIncludedInstallmentCount;
    public int TrackedVolumeCount => Progress.TrackedReleasedIncludedInstallmentCount;
    public int UntrackedVolumeCount => Progress.UntrackedReleasedIncludedInstallmentCount;
    public bool IsComplete => Progress.IsSeriesComplete;
    public int ComputableVolumeCount => Progress.ComputableProgressInstallmentCount;
    public bool HasPartialCoverage => ReleasedVolumeCount > 0 && ComputableVolumeCount < ReleasedVolumeCount;
    public string PartialCoverageLabel => HasPartialCoverage ? $"covers {ComputableVolumeCount} of {ReleasedVolumeCount} volumes" : string.Empty;

    public string CompletedVolumesSummary => ReleasedVolumeCount > 0
        ? $"{CompletedVolumeCount} / {ReleasedVolumeCount} released volumes completed"
        : Installments.Count > 0
            ? "0 released volumes"
            : "No volumes in catalogue";

    public string TrackingSummary => $"{TrackedVolumeCount} tracked, {UntrackedVolumeCount} not tracked";

    public string StatusLabel => IsComplete
        ? "Completed"
        : TrackedVolumeCount > 0 || CompletedVolumeCount > 0
            ? "In progress"
            : "Not started";

    public string StatusBadgeCss => IsComplete
        ? "bg-success"
        : TrackedVolumeCount > 0 || CompletedVolumeCount > 0
            ? "bg-primary"
            : "bg-secondary";

    public string HeadlineProgressText => Progress.ProgressPercentage.HasValue
        ? $"{Progress.ProgressPercentage.Value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : "No computable progress";

    public ProgressBarViewModel ProgressBar => new(
        Current: Progress.ComputableProgressCharacterCount > 0 && Progress.ProgressPercentage.HasValue
            ? (int)Math.Round(Progress.ComputableProgressCharacterCount * (Progress.ProgressPercentage.Value / 100d))
            : 0,
        Total: (int)Math.Min(int.MaxValue, Progress.ComputableProgressCharacterCount),
        Label: HeadlineProgressText,
        IsComplete: Progress.IsSeriesComplete,
        ShowPercentage: Progress.ProgressPercentage.HasValue,
        PercentageOverride: Progress.ProgressPercentage);

    public bool HasNonHeadlineVolumes => Progress.UpcomingInstallmentCount > 0 || Progress.UnknownReleaseInstallmentCount > 0 || Progress.ExcludedInstallmentCount > 0;
    public int UnknownReleaseCount => Progress.UnknownReleaseInstallmentCount;
    public int UpcomingCount => Progress.UpcomingInstallmentCount;
    public int ExcludedCount => Progress.ExcludedInstallmentCount;

    public string TypeToJapanese => MediaType switch
    {
        MediaType.Book => "本",
        MediaType.Anime => "アニメ",
        MediaType.Game => "ゲーム",
        _ => "--"
    };
}

public sealed record AvailableCopyOption(
    Guid Id,
    string Title,
    Guid Version,
    Guid? SourceInstallmentId,
    Guid? SourceInstallmentVersion)
{
    public string ReviewValue => string.Join('|',
        Id,
        Version,
        SourceInstallmentId?.ToString() ?? string.Empty,
        SourceInstallmentVersion?.ToString() ?? string.Empty);
}

public sealed record SeriesInstallmentViewModel(
    Guid Id,
    string Title,
    int OrderKey,
    InstallmentKind Kind,
    bool IsIncluded,
    ReleaseState EffectiveReleaseState,
    DateOnly? EffectiveReleaseDate,
    int? CanonicalCharacterCount,
    Guid Version,
    CatalogueCover? Cover,
    InstallmentProgressResult? ProgressResult,
    IReadOnlyList<SeriesCopyViewModel> Copies)
{
    public bool IsTracked => Copies.Count > 0;
    public bool IsCompleted => Copies.Any(c => c.IsCompleted);

    public double? ProgressFraction => ProgressResult?.ProgressFraction;

    public double? ProgressPercentage => ProgressFraction.HasValue
        ? ProgressFraction.Value * 100d
        : null;

    public ProgressBarViewModel? ProgressBar => ProgressPercentage.HasValue
        ? new ProgressBarViewModel(
            Current: (int)Math.Round(ProgressPercentage.Value),
            Total: 100,
            Label: $"{ProgressPercentage.Value.ToString("0.0", CultureInfo.InvariantCulture)}%",
            IsComplete: ProgressPercentage >= 100d,
            ShowPercentage: true,
            PercentageOverride: ProgressPercentage.Value)
        : null;

    public string StatusBadgeCss => IsCompleted
        ? "completed"
        : IsTracked
            ? "active"
            : "catalogue-only";

    public string StatusBadgeLabel => IsCompleted
        ? "Completed"
        : IsTracked
            ? "Tracked"
            : "Catalogue only";

    public bool IsReleaseUnknown => EffectiveReleaseState == ReleaseState.Unknown;
    public bool IsUpcoming => EffectiveReleaseState == ReleaseState.Upcoming;
    public bool IsReleased => EffectiveReleaseState == ReleaseState.Released;
}

public sealed record SeriesCopyViewModel(
    Guid Id,
    string Title,
    bool IsCompleted,
    int? EffectiveCharacterCount,
    long CharactersRead,
    double MinutesRead,
    double? PositionProgressFraction,
    CatalogueCover? Cover)
{
    public double? ProgressPercentage => IsCompleted
        ? 100d
        : PositionProgressFraction.HasValue
            ? Math.Clamp(PositionProgressFraction.Value * 100d, 0d, 100d)
            : EffectiveCharacterCount is > 0
                ? Math.Clamp((double)CharactersRead / EffectiveCharacterCount.Value * 100d, 0d, 100d)
                : null;

    public string ReadingProgressSummary => EffectiveCharacterCount is > 0
        ? $"{CharactersRead:N0} / {EffectiveCharacterCount.Value:N0} ch"
        : $"{CharactersRead:N0} ch";
}
