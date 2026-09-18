using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public interface IJitenCandidateScorer
{
    JitenMatchResult Score(
        ParsedMediaTitle parsedTitle,
        IEnumerable<JitenMatchCandidate> candidates,
        int? authoritativeTtsuTotal = null,
        int filteredIncompatibleCount = 0);
}

