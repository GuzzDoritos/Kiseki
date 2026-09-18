using Kiseki.Core.DTOs;
using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public interface IJitenSelectionResolver
{
    Task<JitenSelectionResult> ResolveAsync(
        int parentDeckId,
        int? subdeckId = null,
        CancellationToken cancellationToken = default);

    JitenSelectionResult Resolve(
        JitenDeckDetailDTO detail,
        int parentDeckId,
        int? subdeckId = null);
}

