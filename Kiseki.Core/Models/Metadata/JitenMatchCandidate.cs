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
    public JitenCoverEvidence CoverEvidence { get; init; } = JitenCoverEvidence.None;

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

    public static JitenMatchCandidate FromSelection(JitenMediaSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return new JitenMatchCandidate
        {
            DeckId = selection.DeckId,
            SubdeckId = selection.SubdeckId,
            OriginalTitle = selection.OriginalTitle,
            RomajiTitle = selection.RomajiTitle,
            EnglishTitle = selection.EnglishTitle,
            CharacterCount = selection.CharacterCount,
            ChildrenDeckCount = selection.ChildrenDeckCount,
            CoverUrl = selection.CoverUrl,
            CoverEvidence = selection.CoverEvidence
        };
    }

    public static JitenMatchCandidate FromDeck(JitenDeckDTO deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return FromSelection(JitenMediaSelection.FromDeck(deck));
    }

    public static JitenMatchCandidate FromSubdeck(JitenDeckDTO parentDeck, JitenDeckDTO subdeck)
    {
        ArgumentNullException.ThrowIfNull(parentDeck);
        ArgumentNullException.ThrowIfNull(subdeck);
        return FromSelection(JitenMediaSelection.FromSubdeck(parentDeck, subdeck));
    }
}

