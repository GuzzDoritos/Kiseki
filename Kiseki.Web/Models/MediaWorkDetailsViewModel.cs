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
    int? ManualCharacterCountOverride,
    string? JitenCoverUrl,
    int CharactersRead,
    int TotalCharacters,
    bool IsCompleted,
    IReadOnlyList<ImmersionLogViewModel> Logs)
{
    public bool HasJitenLink => JitenDeckId.HasValue;
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

    public string JitenLinkLabel => JitenDeckId switch
    {
        null => "Not linked",
        int deckId when JitenSubdeckId is int subdeckId => $"Deck {deckId} / subdeck {subdeckId}",
        int deckId => $"Deck {deckId}"
    };

    public string CharacterTotalSource => ManualCharacterCountOverride.HasValue
        ? "Manual override"
        : JitenCharacterCount.HasValue
            ? "Jiten"
            : "Not set";

    public string TotalTimeLabel => ImmersionLogViewModel.FormatDuration(TotalTimeMinutes);

    public static MediaWorkDetailsViewModel Create(MediaWork work)
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
            work.ManualCharacterCountOverride,
            work.JitenCoverUrl,
            work.CurrentCharactersRead,
            work.TotalCharacters,
            work.IsCompleted,
            logs);
    }
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
