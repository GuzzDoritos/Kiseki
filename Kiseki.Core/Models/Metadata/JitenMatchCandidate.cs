using Kiseki.Core.DTOs;
using Kiseki.Core.Models;

namespace Kiseki.Core.Models.Metadata;

public sealed record JitenMatchCandidate
{
    public int DeckId { get; init; }
    public int? SubdeckId { get; init; }
    public string OriginalTitle { get; init; } = string.Empty;
    public string RomajiTitle { get; init; } = string.Empty;
    public string EnglishTitle { get; init; } = string.Empty;
    public int CharacterCount { get; init; }
    public int ChildrenDeckCount { get; init; }
    public string? CoverUrl { get; init; }

    public bool IsSubdeck => SubdeckId.HasValue;
    public bool IsStandalone => !SubdeckId.HasValue && ChildrenDeckCount == 0;

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OriginalTitle))
            {
                return OriginalTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(EnglishTitle))
            {
                return EnglishTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(RomajiTitle))
            {
                return RomajiTitle.Trim();
            }

            return $"Jiten deck {SubdeckId ?? DeckId}";
        }
    }

    public static JitenMatchCandidate FromDeck(JitenDeckDTO deck)
    {
        ArgumentNullException.ThrowIfNull(deck);

        return new JitenMatchCandidate
        {
            DeckId = deck.DeckId,
            SubdeckId = null,
            OriginalTitle = deck.OriginalTitle?.Trim() ?? string.Empty,
            RomajiTitle = deck.RomajiTitle?.Trim() ?? string.Empty,
            EnglishTitle = deck.EnglishTitle?.Trim() ?? string.Empty,
            CharacterCount = deck.CharacterCount,
            ChildrenDeckCount = deck.ChildrenDeckCount,
            CoverUrl = deck.CoverName
        };
    }

    public static JitenMatchCandidate FromSubdeck(JitenDeckDTO parentDeck, JitenDeckDTO subdeck)
    {
        ArgumentNullException.ThrowIfNull(parentDeck);
        ArgumentNullException.ThrowIfNull(subdeck);

        return new JitenMatchCandidate
        {
            DeckId = parentDeck.DeckId,
            SubdeckId = subdeck.DeckId,
            OriginalTitle = subdeck.OriginalTitle?.Trim() ?? string.Empty,
            RomajiTitle = subdeck.RomajiTitle?.Trim() ?? string.Empty,
            EnglishTitle = subdeck.EnglishTitle?.Trim() ?? string.Empty,
            CharacterCount = subdeck.CharacterCount,
            ChildrenDeckCount = subdeck.ChildrenDeckCount,
            CoverUrl = !string.IsNullOrWhiteSpace(subdeck.CoverName) ? subdeck.CoverName : parentDeck.CoverName
        };
    }
}

