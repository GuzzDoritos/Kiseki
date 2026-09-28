using Kiseki.Core.Entities;

namespace Kiseki.Web.Models;

public sealed record SeriesListItemViewModel(
    Guid Id,
    string Title,
    MediaType MediaType,
    string? CoverUrl,
    string? EffectiveCoverUrl,
    int? JitenDeckId,
    int InstallmentsCount,
    int CompletedInstallmentsCount,
    int TotalCharacters,
    int CharactersRead,
    double ProgressPercentage,
    bool IsCompleted)
{
    public string TypeToJapanese => MediaType switch
    {
        MediaType.Book => "本",
        MediaType.Anime => "アニメ",
        MediaType.Game => "ゲーム",
        _ => MediaType.ToString()
    };

    public bool IsJitenLinked => JitenDeckId.HasValue;

    public string ProgressLabel => TotalCharacters > 0
        ? $"{CharactersRead:N0} / {TotalCharacters:N0} ch"
        : $"{CharactersRead:N0} ch";

    public string VolumeProgressText => $"{CompletedInstallmentsCount} / {InstallmentsCount} {(InstallmentsCount == 1 ? "volume" : "volumes")}";

    public static SeriesListItemViewModel FromEntity(MediaSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var isCompleted = series.Installments.Count > 0 && series.Installments.All(i => i.IsCompleted);

        return new SeriesListItemViewModel(
            series.Id,
            series.Title,
            series.MediaType,
            series.CoverUrl,
            series.EffectiveCoverUrl,
            series.JitenDeckId,
            series.Installments.Count,
            series.CompletedInstallmentsCount,
            series.TotalCharacters,
            series.CurrentCharactersRead,
            series.ProgressPercentage,
            isCompleted);
    }
}
