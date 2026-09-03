using Core.Entities;

namespace Web.Models;

public sealed record MediaWorkListItemViewModel(
    Guid Id,
    string Title,
    string? SeriesTitle,
    MediaType MediaType,
    int CharactersRead,
    int TotalCharacters,
    bool IsJitenLinked);
