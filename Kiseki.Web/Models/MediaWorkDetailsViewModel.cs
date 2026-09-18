using Kiseki.Core.Entities;

namespace Kiseki.Web.Models;

public sealed record MediaWorkDetailsViewModel(
    Guid Id,
    string Title,
    MediaType MediaType,
    string? SeriesTitle,
    int? JitenDeckId,
    int? JitenSubdeckId,
    int? JitenCharacterCount,
    int? TtsuCharacterCount,
    int? ManualCharacterCountOverride,
    string? CoverUrl,
    MediaCoverSource CoverSource,
    string? CoverProviderItemId,
    int CharactersRead,
    int TotalCharacters,
    bool IsCompleted,
    IReadOnlyList<ImmersionLogViewModel> Logs,
    int? CurrentCharacterPosition,
    double? PositionProgressPercentage)
{
    public bool HasJitenLink => JitenDeckId.HasValue;
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverUrl);
    public int SessionCount => Logs.Count;
    public double TotalTimeMinutes => Logs.Sum(log => log.TimeSpentMinutes);

    public string StatusLabel => IsCompleted
        ? "Completed"
        : CharactersRead > 0
            ? MediaType switch
            {
                MediaType.Book => "Reading",
                MediaType.Anime => "Watching",
                MediaType.Game => "Playing",
                _ => "In progress"
            }
            : "Not started";

    public string StatusCssClass => IsCompleted
        ? "completed"
        : CharactersRead > 0
            ? "active"
            : "idle";

    public string JitenLinkLabel => JitenDeckId switch
    {
        null => "Not linked",
        int deckId when JitenSubdeckId is int subdeckId => $"Deck {deckId} / subdeck {subdeckId}",
        int deckId => $"Deck {deckId}"
    };

    public string CoverSourceLabel => CoverSource switch
    {
        MediaCoverSource.UserOverride => "Custom cover",
        MediaCoverSource.LegacyUnknown => "Legacy cover",
        MediaCoverSource.JitenSpecific => "Jiten volume cover",
        MediaCoverSource.JitenParentFallback => "Jiten series fallback",
        MediaCoverSource.GoogleBooks => "Google Books volume cover",
        _ => "No cover"
    };

    public string CharacterTotalSource => ManualCharacterCountOverride.HasValue
        ? "Manual override"
        : TtsuCharacterCount.HasValue
            ? "ッツ"
            : JitenCharacterCount.HasValue
                ? "Jiten"
                : "None";

    public string TotalTimeLabel
    {
        get
        {
            var totalMinutes = (long)Math.Round(TotalTimeMinutes);
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            return hours > 0 ? $"{hours}h {minutes:D2}m" : $"{minutes}m";
        }
    }

    public static MediaWorkDetailsViewModel Create(
        MediaWork work,
        TtsuBinding? binding = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        var logs = work.Logs
            .OrderByDescending(log => log.Date)
            .ThenByDescending(log => log.Id)
            .Select(log => new ImmersionLogViewModel(
                log.Id,
                log.Date,
                log.CharactersRead,
                log.TimeSpentMinutes,
                log.Source))
            .ToList();

        return new MediaWorkDetailsViewModel(
            work.Id,
            work.Title,
            work.MediaType,
            work.MediaSeries?.Title,
            work.JitenDeckId,
            work.JitenSubdeckId,
            work.JitenCharacterCount,
            work.TtsuCharacterCount,
            work.ManualCharacterCountOverride,
            work.CoverUrl,
            work.CoverSource,
            work.CoverProviderItemId,
            work.CurrentCharactersRead,
            work.TotalCharacters,
            work.IsCompleted,
            logs,
            binding?.CurrentCharacterPosition,
            binding?.ProgressFraction * 100d);
    }

    public static MediaWorkDetailsViewModel FromEntity(MediaWork work, TtsuBinding? binding = null) =>
        Create(work, binding);
}

public sealed record ImmersionLogViewModel(
    Guid Id,
    DateOnly Date,
    int CharactersRead,
    double TimeSpentMinutes,
    string Source)
{
    public string TimeSpentLabel => FormatDuration(TimeSpentMinutes);

    public string SourceLabel => string.IsNullOrWhiteSpace(Source)
        ? "Unknown"
        : Source.Trim().ToUpperInvariant();

    public static string FormatDuration(double minutes)
    {
        if (minutes <= 0)
        {
            return "0 min";
        }

        if (minutes < 60)
        {
            return $"{minutes:N1} min";
        }

        var roundedMinutes = (int)Math.Round(minutes);
        var hours = roundedMinutes / 60;
        var remainingMinutes = roundedMinutes % 60;
        return remainingMinutes == 0
            ? $"{hours:N0} h"
            : $"{hours:N0} h {remainingMinutes:N0} min";
    }
}
