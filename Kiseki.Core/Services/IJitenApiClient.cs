using Kiseki.Core.DTOs;

namespace Kiseki.Core.Services;

public interface IJitenApiClient
{
    Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JitenDeckDTO>> SearchBooksBoundedAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return SearchBooksAsync(query, cancellationToken);
    }

    Task<JitenDeckDetailDTO?> GetDeckDetailAsync(
        int deckId,
        CancellationToken cancellationToken = default);

    async Task<JitenDeckCatalogueFetchResult> GetDeckCatalogueAsync(
        int deckId,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetDeckDetailAsync(deckId, cancellationToken);
        var count = detail?.SubDecks.Count ?? 0;
        var declared = Math.Max(detail?.MainDeck?.ChildrenDeckCount ?? 0,
            detail?.ParentDeck?.ChildrenDeckCount ?? 0);
        var expected = Math.Max(count, declared);
        var complete = detail is not null && count >= expected &&
                       detail.SubDecks.Select(item => item.DeckId).Distinct().Count() == count;
        return new(detail, complete, expected, count,
            detail is null ? "Jiten could not find the requested deck." :
            complete ? null : "The client could not prove that every Jiten catalogue page was loaded.");
    }

    Task<JitenFranchiseDTO?> GetFranchiseAsync(
        int deckId,
        CancellationToken cancellationToken = default);
}
