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
    public string? ParentOriginalTitle { get; init; }
    public string? ParentRomajiTitle { get; init; }
    public string? ParentEnglishTitle { get; init; }
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
            var childTitle = FirstNonEmpty(OriginalTitle, EnglishTitle, RomajiTitle);

            if (IsSubdeck)
            {
                var parentTitle = FirstNonEmpty(ParentOriginalTitle, ParentEnglishTitle, ParentRomajiTitle);
                if (!string.IsNullOrWhiteSpace(parentTitle))
                {
                    if (string.IsNullOrWhiteSpace(childTitle))
                    {
                        return $"{parentTitle.Trim()} — Subdeck {SubdeckId}";
                    }

                    var trimmedChild = childTitle.Trim();
                    var trimmedParent = parentTitle.Trim();
                    if (trimmedChild.StartsWith(trimmedParent, StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmedChild;
                    }

                    return $"{trimmedParent} — {trimmedChild}";
                }
            }

            if (!string.IsNullOrWhiteSpace(childTitle))
            {
                return childTitle.Trim();
            }

            return $"Jiten deck {SubdeckId ?? DeckId}";
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

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
            ParentOriginalTitle = selection.ParentOriginalTitle,
            ParentRomajiTitle = selection.ParentRomajiTitle,
            ParentEnglishTitle = selection.ParentEnglishTitle,
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

