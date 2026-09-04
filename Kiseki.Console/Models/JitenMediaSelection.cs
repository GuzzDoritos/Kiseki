using Kiseki.Core.Entities;

namespace Kiseki.Console.Models;

public sealed record JitenMediaSelection(
    int DeckId,
    int? SubdeckId,
    string Title,
    int CharacterCount)
{
    public bool IsSubdeck => SubdeckId.HasValue;

    public void ApplyTo(MediaWork mediaWork)
    {
        if (SubdeckId is int subdeckId)
        {
            mediaWork.LinkToJitenSubdeck(DeckId, subdeckId, CharacterCount);
        }
        else
        {
            mediaWork.LinkToJitenDeck(DeckId, CharacterCount);
        }
    }
}
