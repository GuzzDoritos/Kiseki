using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public interface IJitenSelectionResolver
{
    Task<JitenSelectionResult> ResolveAsync(
        int parentDeckId,
        int? subdeckId = null,
        CancellationToken cancellationToken = default);
}

