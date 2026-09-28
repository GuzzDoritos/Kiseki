using Kiseki.Core.Entities;
using Kiseki.Core.Models.Pace;
using Kiseki.Core.Services;

namespace Kiseki.Web.Models;

public sealed record SeriesInstallmentViewModel(
    Guid Id,
    int SequenceNumber,
    string Title,
    int? JitenSubdeckId,
    int JitenCharacterCount,
    string? CoverUrl,
    Guid? MediaWorkId,
    string? MediaWorkTitle,
    int EffectiveTotalCharacters,
    int EffectiveCharactersRead,
    double ProgressPercentage,
    bool IsCompleted,
    string? EffectiveCoverUrl,
    int RemainingCharacters = 0,
    ReadingTimeEstimate? TimeEstimate = null);

public sealed record AvailableLibraryWorkViewModel(
    Guid Id,
    string Title,
    int TotalCharacters,
    int CurrentCharactersRead,
    bool IsCompleted);

public sealed record SeriesDetailsViewModel(
    Guid Id,
    string Title,
    MediaType MediaType,
    string? CoverUrl,
    string? EffectiveCoverUrl,
    int? JitenDeckId,
    int TotalCharacters,
    int CurrentCharactersRead,
    int CompletedInstallmentsCount,
    double ProgressPercentage,
    IReadOnlyList<SeriesInstallmentViewModel> Installments,
    IReadOnlyList<AvailableLibraryWorkViewModel> AvailableWorks,
    ReadingPaceProfile? PaceProfile = null,
    ReadingTimeEstimate? TimeEstimate = null,
    int RemainingCharacters = 0,
    int UncountedInstallmentsCount = 0)
{
    public static SeriesDetailsViewModel Create(
        MediaSeries series,
        IReadOnlyList<MediaWork> availableWorks,
        ReadingPaceProfile? paceProfile = null,
        IReadingPaceService? paceService = null)
    {
        ArgumentNullException.ThrowIfNull(series);

        var installments = series.Installments
            .OrderBy(i => i.SequenceNumber)
            .Select(i =>
            {
                var remaining = i.IsCompleted ? 0 : Math.Max(0, i.EffectiveTotalCharacters - i.EffectiveCharactersRead);
                var estimate = paceService != null && paceProfile != null
                    ? paceService.EstimateInstallmentTime(i, paceProfile)
                    : null;

                return new SeriesInstallmentViewModel(
                    i.Id,
                    i.SequenceNumber,
                    i.Title,
                    i.JitenSubdeckId,
                    i.JitenCharacterCount,
                    i.CoverUrl,
                    i.MediaWorkId,
                    i.MediaWork?.Title,
                    i.EffectiveTotalCharacters,
                    i.EffectiveCharactersRead,
                    i.ProgressPercentage,
                    i.IsCompleted,
                    i.EffectiveCoverUrl,
                    remaining,
                    estimate);
            })
            .ToList();

        var available = (availableWorks ?? [])
            .Select(w => new AvailableLibraryWorkViewModel(
                w.Id,
                w.Title,
                w.TotalCharacters,
                w.CurrentCharactersRead,
                w.IsCompleted))
            .ToList();

        var seriesEstimate = paceService != null && paceProfile != null
            ? paceService.EstimateSeriesTime(series, paceProfile)
            : null;

        var remainingCharacters = seriesEstimate?.RemainingCharacters
            ?? installments.Sum(i => i.RemainingCharacters);

        var uncountedCount = seriesEstimate?.UncountedVolumesCount
            ?? installments.Count(i => !i.IsCompleted && i.EffectiveTotalCharacters <= 0);

        return new SeriesDetailsViewModel(
            series.Id,
            series.Title,
            series.MediaType,
            series.CoverUrl,
            series.EffectiveCoverUrl,
            series.JitenDeckId,
            series.TotalCharacters,
            series.CurrentCharactersRead,
            series.CompletedInstallmentsCount,
            series.ProgressPercentage,
            installments,
            available,
            paceProfile,
            seriesEstimate,
            remainingCharacters,
            uncountedCount);
    }
}

