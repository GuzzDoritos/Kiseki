using Kiseki.Core.DTOs;

namespace Kiseki.Core.Services;

public interface IJitenApiClient
{
    Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query);

    Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId);

    Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId);
}
