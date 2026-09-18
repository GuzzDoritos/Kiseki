using System.Globalization;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public static class TtsuProgressNormalizer
{
    private const double RatioTolerance = 1e-12;

    public static IReadOnlyList<TtsuProgressSnapshot> Normalize(TtsuBookContainer book)
    {
        ArgumentNullException.ThrowIfNull(book);

        var snapshots = book.ProgressEntries.Select(Normalize).ToList();
        if (snapshots.Count == 0)
        {
            return [];
        }

        var latestKnownRevision = snapshots.Max(snapshot => snapshot.Revision);
        return snapshots
            .Where(snapshot => snapshot.Revision is null || snapshot.Revision == latestKnownRevision)
            .Distinct()
            .OrderBy(snapshot => snapshot.Revision)
            .ThenBy(snapshot => snapshot.CharacterPosition)
            .ThenBy(snapshot => snapshot.ProgressFraction)
            .ToList();
    }

    public static int? ResolveAuthoritativeTotal(TtsuBookContainer book)
    {
        var eligibleTotals = Normalize(book)
            .Where(snapshot =>
                snapshot.InferenceKind is TtsuTotalInferenceKind.ExactRatio or TtsuTotalInferenceKind.CompletionAdjusted &&
                snapshot.InferredTotalCharacters is > 0)
            .Select(snapshot => snapshot.InferredTotalCharacters!.Value)
            .Distinct()
            .ToList();

        return eligibleTotals.Count == 1 ? eligibleTotals[0] : null;
    }

    public static TtsuProgressSnapshot Normalize(TtsuProgressDTO progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (progress.ExploredCharacterCount is null or < 0)
        {
            throw new InvalidDataException("TTSU progress needs a non-negative exploredCharCount.");
        }

        if (progress.LastBookmarkModified < 0)
        {
            throw new InvalidDataException("TTSU progress contains a negative bookmark revision.");
        }

        var (fraction, isRoundedPercentage) = ReadFraction(progress.Progress);
        if (!double.IsFinite(fraction) || fraction is < 0 or > 1)
        {
            throw new InvalidDataException("TTSU progress must be between 0 and 1.");
        }

        var inferenceKind = isRoundedPercentage
            ? TtsuTotalInferenceKind.RoundedPercentage
            : fraction == 1
                ? TtsuTotalInferenceKind.CompletionAdjusted
                : TtsuTotalInferenceKind.ExactRatio;

        var total = InferTotal(progress.ExploredCharacterCount.Value, fraction, inferenceKind);
        return new(
            progress.ExploredCharacterCount.Value,
            fraction,
            progress.LastBookmarkModified > 0 ? progress.LastBookmarkModified : null,
            total,
            inferenceKind,
            progress.ExporterVersion,
            progress.DatabaseVersion);
    }

    private static (double Fraction, bool IsRoundedPercentage) ReadFraction(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
        {
            return (numeric, false);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("TTSU progress is empty.");
            }

            var percentage = text.EndsWith('%');
            var numberText = percentage ? text[..^1] : text;
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
            {
                throw new InvalidDataException($"TTSU progress '{text}' is not numeric.");
            }

            return (percentage ? numeric / 100d : numeric, true);
        }

        throw new InvalidDataException("TTSU progress must be a number or percentage string.");
    }

    private static int? InferTotal(int position, double fraction, TtsuTotalInferenceKind kind)
    {
        if (position == 0 || fraction == 0 || kind == TtsuTotalInferenceKind.RoundedPercentage)
        {
            return null;
        }

        if (kind == TtsuTotalInferenceKind.CompletionAdjusted)
        {
            return position == int.MaxValue ? null : position + 1;
        }

        var raw = position / fraction;
        if (!double.IsFinite(raw) || raw <= 0 || raw > int.MaxValue)
        {
            return null;
        }

        var candidate = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        if (candidate < position || candidate == 0 ||
            Math.Abs((double)position / candidate - fraction) > RatioTolerance)
        {
            return null;
        }

        return candidate;
    }
}
