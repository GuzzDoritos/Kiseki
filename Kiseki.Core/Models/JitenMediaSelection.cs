using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;

namespace Kiseki.Core.Models;

public enum JitenTitleChoice
{
    KeepCurrent,
    Original,
    English,
    Romaji
}

public sealed record JitenMediaSelection(
    int DeckId,
    int? SubdeckId,
    string OriginalTitle,
    string RomajiTitle,
    string EnglishTitle,
    int CharacterCount,
    string? CoverUrl,
    int ChildrenDeckCount)
{
    public bool IsSubdeck => SubdeckId.HasValue;

    public string DisplayTitle => FirstNonEmpty(
        OriginalTitle,
        EnglishTitle,
        RomajiTitle) ?? $"Jiten deck {SubdeckId ?? DeckId}";

    public static JitenMediaSelection FromDeck(JitenDeckDTO deck)
    {
        ArgumentNullException.ThrowIfNull(deck);

        return Create(
            deck.DeckId,
            subdeckId: null,
            deck,
            fallbackCoverUrl: null);
    }

    public static JitenMediaSelection FromSubdeck(
        JitenDeckDTO parentDeck,
        JitenDeckDTO subdeck)
    {
        ArgumentNullException.ThrowIfNull(parentDeck);
        ArgumentNullException.ThrowIfNull(subdeck);

        return Create(
            parentDeck.DeckId,
            subdeck.DeckId,
            subdeck,
            parentDeck.CoverName);
    }

    public bool HasTitle(JitenTitleChoice choice)
    {
        return choice switch
        {
            JitenTitleChoice.KeepCurrent => true,
            JitenTitleChoice.Original => !string.IsNullOrWhiteSpace(OriginalTitle),
            JitenTitleChoice.English => !string.IsNullOrWhiteSpace(EnglishTitle),
            JitenTitleChoice.Romaji => !string.IsNullOrWhiteSpace(RomajiTitle),
            _ => false
        };
    }

    public void ApplyTo(
        MediaWork mediaWork,
        JitenTitleChoice titleChoice = JitenTitleChoice.KeepCurrent)
    {
        ArgumentNullException.ThrowIfNull(mediaWork);

        var replacementTitle = ResolveTitle(titleChoice);

        if (SubdeckId is int subdeckId)
        {
            mediaWork.LinkToJitenSubdeck(
                DeckId,
                subdeckId,
                CharacterCount,
                CoverUrl);
        }
        else
        {
            mediaWork.LinkToJitenDeck(DeckId, CharacterCount, CoverUrl);
        }

        if (replacementTitle is not null)
        {
            mediaWork.Title = replacementTitle;
        }
    }

    private static JitenMediaSelection Create(
        int deckId,
        int? subdeckId,
        JitenDeckDTO selectedDeck,
        string? fallbackCoverUrl)
    {
        return new JitenMediaSelection(
            deckId,
            subdeckId,
            selectedDeck.OriginalTitle?.Trim() ?? string.Empty,
            selectedDeck.RomajiTitle?.Trim() ?? string.Empty,
            selectedDeck.EnglishTitle?.Trim() ?? string.Empty,
            selectedDeck.CharacterCount,
            NormalizeCoverUrl(selectedDeck.CoverName) ?? NormalizeCoverUrl(fallbackCoverUrl),
            selectedDeck.ChildrenDeckCount);
    }

    private string? ResolveTitle(JitenTitleChoice choice)
    {
        var title = choice switch
        {
            JitenTitleChoice.KeepCurrent => null,
            JitenTitleChoice.Original => OriginalTitle,
            JitenTitleChoice.English => EnglishTitle,
            JitenTitleChoice.Romaji => RomajiTitle,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };

        if (choice != JitenTitleChoice.KeepCurrent && string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException(
                $"The selected Jiten title variant '{choice}' is not available.");
        }

        return title?.Trim();
    }

    private static string? NormalizeCoverUrl(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) ||
            coverUrl.Trim().Equals("nocover.jpg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = coverUrl.Trim();
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static string? FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }
}
