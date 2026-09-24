using Kiseki.Core.Entities;

namespace Kiseki.Core.Services;

/// <summary>Pure canonical book-progress calculation. It never mutates entities or queries persistence.</summary>
public static class SeriesProgressCalculator
{
    public static SeriesProgressResult Calculate(SeriesProgressInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var installments = input.Installments.Select(CalculateInstallment).ToList();
        var eligible = installments.Where(item => item.IsIncluded && item.EffectiveReleaseState == ReleaseState.Released).ToList();
        var knownTotals = eligible.Where(item => item.CanonicalCharacterCount is > 0).ToList();
        var computable = knownTotals.Where(item => item.ProgressFraction.HasValue).ToList();
        var unknownProgress = knownTotals.Where(item => !item.ProgressFraction.HasValue).ToList();
        var computableWeight = computable.Sum(item => (long)item.CanonicalCharacterCount!.Value);
        var weightedProgress = computable.Sum(item => item.CanonicalCharacterCount!.Value * item.ProgressFraction!.Value);
        var completed = eligible.Count(item => item.IsCompleted);
        var tracked = eligible.Count(item => item.IsTracked);

        return new SeriesProgressResult(
            input.SeriesId,
            input.MediaType,
            installments,
            ProgressPercentage: computableWeight == 0 ? null : weightedProgress / computableWeight * 100d,
            ReleasedIncludedInstallmentCount: eligible.Count,
            CompletedReleasedIncludedInstallmentCount: completed,
            TrackedReleasedIncludedInstallmentCount: tracked,
            UntrackedReleasedIncludedInstallmentCount: eligible.Count - tracked,
            KnownCanonicalTotalInstallmentCount: knownTotals.Count,
            UnknownCanonicalTotalInstallmentCount: eligible.Count - knownTotals.Count,
            KnownCanonicalCharacterCount: knownTotals.Sum(item => (long)item.CanonicalCharacterCount!.Value),
            ComputableProgressInstallmentCount: computable.Count,
            ComputableProgressCharacterCount: computableWeight,
            UnknownProgressInstallmentCount: unknownProgress.Count,
            UnknownProgressCharacterCount: unknownProgress.Sum(item => (long)item.CanonicalCharacterCount!.Value),
            UpcomingInstallmentCount: installments.Count(item => item.EffectiveReleaseState == ReleaseState.Upcoming),
            UnknownReleaseInstallmentCount: installments.Count(item => item.EffectiveReleaseState == ReleaseState.Unknown),
            ExcludedInstallmentCount: installments.Count(item => !item.IsIncluded),
            LifetimeCharactersRead: installments.Sum(item => item.LifetimeCharactersRead),
            LifetimeMinutes: installments.Sum(item => item.LifetimeMinutes));
    }

    private static InstallmentProgressResult CalculateInstallment(InstallmentProgressInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var copies = input.Copies ?? [];
        var isCompleted = copies.Any(copy => copy.IsCompleted);
        var knownFractions = copies
            .Select(CopyProgressFraction)
            .Where(fraction => fraction.HasValue)
            .Select(fraction => fraction!.Value)
            .ToList();
        // Catalogue-only released installments are known zero-progress entries.
        // A tracked installment with no usable edition total stays unknown.
        double? progress = isCompleted ? 1d : copies.Count == 0 ? 0d :
            knownFractions.Count == 0 ? null : knownFractions.Max();

        return new InstallmentProgressResult(
            input.InstallmentId,
            input.IsIncluded,
            input.EffectiveReleaseState,
            input.CanonicalCharacterCount,
            copies.Count,
            isCompleted,
            progress,
            copies.Sum(copy => (long)copy.CharactersRead),
            copies.Sum(copy => copy.MinutesRead));
    }

    private static double? CopyProgressFraction(CopyProgressInput copy)
    {
        if (copy.IsCompleted)
        {
            return 1d;
        }

        if (copy.PositionProgressFraction is double position)
        {
            return Math.Clamp(position, 0d, 1d);
        }

        return copy.EffectiveCharacterCount is > 0
            ? Math.Clamp(copy.CharactersRead / (double)copy.EffectiveCharacterCount.Value, 0d, 1d)
            : null;
    }
}

public sealed record SeriesProgressInput(
    Guid SeriesId,
    MediaType MediaType,
    IReadOnlyList<InstallmentProgressInput> Installments);

public sealed record InstallmentProgressInput(
    Guid InstallmentId,
    bool IsIncluded,
    ReleaseState EffectiveReleaseState,
    int? CanonicalCharacterCount,
    IReadOnlyList<CopyProgressInput> Copies);

public sealed record CopyProgressInput(
    Guid CopyId,
    bool IsCompleted,
    int? EffectiveCharacterCount,
    long CharactersRead,
    double MinutesRead,
    double? PositionProgressFraction = null);

public sealed record InstallmentProgressResult(
    Guid InstallmentId,
    bool IsIncluded,
    ReleaseState EffectiveReleaseState,
    int? CanonicalCharacterCount,
    int CopyCount,
    bool IsCompleted,
    double? ProgressFraction,
    long LifetimeCharactersRead,
    double LifetimeMinutes)
{
    public bool IsTracked => CopyCount > 0;
}

public sealed record SeriesProgressResult(
    Guid SeriesId,
    MediaType MediaType,
    IReadOnlyList<InstallmentProgressResult> Installments,
    double? ProgressPercentage,
    int ReleasedIncludedInstallmentCount,
    int CompletedReleasedIncludedInstallmentCount,
    int TrackedReleasedIncludedInstallmentCount,
    int UntrackedReleasedIncludedInstallmentCount,
    int KnownCanonicalTotalInstallmentCount,
    int UnknownCanonicalTotalInstallmentCount,
    long KnownCanonicalCharacterCount,
    int ComputableProgressInstallmentCount,
    long ComputableProgressCharacterCount,
    int UnknownProgressInstallmentCount,
    long UnknownProgressCharacterCount,
    int UpcomingInstallmentCount,
    int UnknownReleaseInstallmentCount,
    int ExcludedInstallmentCount,
    long LifetimeCharactersRead,
    double LifetimeMinutes)
{
    /// <summary>Compatibility alias for the released-and-included headline denominator.</summary>
    public int EligibleInstallmentCount => ReleasedIncludedInstallmentCount;

    /// <summary>Compatibility alias for catalogue-only entries in the headline denominator.</summary>
    public int UntrackedEligibleInstallmentCount => UntrackedReleasedIncludedInstallmentCount;

    /// <summary>An empty eligible catalogue is not considered a completed series.</summary>
    public bool IsSeriesComplete => ReleasedIncludedInstallmentCount > 0 &&
        CompletedReleasedIncludedInstallmentCount == ReleasedIncludedInstallmentCount;
}
