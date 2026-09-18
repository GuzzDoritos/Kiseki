using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public interface IJitenMatchService
{
    Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
        IReadOnlyList<JitenMatchRequest> requests,
        TimeSpan timeBudget,
        CancellationToken cancellationToken = default);
}

