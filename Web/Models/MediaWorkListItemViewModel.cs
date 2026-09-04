using Core.Entities;

namespace Web.Models;

public sealed record MediaWorkListItemViewModel(
    Guid Id,
    string Title,
    string? SeriesTitle,
    MediaType MediaType,
    int CharactersRead,
    int TotalCharacters,
    bool IsJitenLinked,
    bool IsCompleted,
    int SessionCount)
{
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
            : "not-started";

    public string TypeAbbreviation => MediaType switch
    {
        MediaType.Book => "本",
        MediaType.Anime => "アニメ",
        MediaType.Game => "ゲーム",
        _ => "--"
    };
}
