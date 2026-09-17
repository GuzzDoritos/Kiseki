using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public sealed class JitenSelectionResolver : IJitenSelectionResolver
{
    private readonly IJitenApiClient _jitenApiClient;

    public JitenSelectionResolver(IJitenApiClient jitenApiClient)
    {
        _jitenApiClient = jitenApiClient ?? throw new ArgumentNullException(nameof(jitenApiClient));
    }

    public async Task<JitenSelectionResult> ResolveAsync(
        int parentDeckId,
        int? subdeckId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (parentDeckId <= 0)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.InvalidDeckId,
                "Parent deck ID must be greater than zero.");
        }

        if (subdeckId.HasValue && subdeckId.Value <= 0)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.InvalidDeckId,
                "Subdeck ID must be greater than zero.");
        }

        var detail = await _jitenApiClient.GetDeckDetailAsync(parentDeckId, cancellationToken);

        if (detail is null)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.DeckNotFound,
                "Jiten could not verify the selected deck.");
        }

        var parent = new[] { detail.MainDeck, detail.ParentDeck }
            .FirstOrDefault(deck => deck?.DeckId == parentDeckId);

        if (parent is null)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.MismatchedParent,
                "The requested parent deck was not returned by Jiten.");
        }

        if (subdeckId is int requestedSubdeckId)
        {
            var subdeck = detail.SubDecks
                .FirstOrDefault(d => d.DeckId == requestedSubdeckId);

            if (subdeck is null)
            {
                return JitenSelectionResult.Failed(
                    JitenSelectionStatus.SubdeckNotFound,
                    "The selected Jiten subdeck no longer exists.");
            }

            return JitenSelectionResult.Succeeded(
                JitenMediaSelection.FromSubdeck(parent, subdeck));
        }

        // When no subdeck ID is supplied, allow the parent only when it has no children and the fresh response has no subdecks.
        if (parent.ChildrenDeckCount > 0 || detail.SubDecks.Count > 0)
        {
            return JitenSelectionResult.Failed(
                JitenSelectionStatus.ParentHasChildren,
                "Cannot link a series deck directly when subdecks exist. Choose a specific subdeck.");
        }

        return JitenSelectionResult.Succeeded(
            JitenMediaSelection.FromDeck(parent));
    }
}

