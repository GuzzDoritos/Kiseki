using Kiseki.Core.DTOs;

namespace Kiseki.Core.Services;

public interface IJitenApiClient
{
    Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<JitenDeckDetailDTO?> GetDeckDetailAsync(
        int deckId,
        CancellationToken cancellationToken = default);

    Task<JitenFranchiseDTO?> GetFranchiseAsync(
        int deckId,
        CancellationToken cancellationToken = default);
}
