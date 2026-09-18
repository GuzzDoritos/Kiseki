namespace Kiseki.Core.Models.Metadata;

public enum MatchConfidence
{
    None,
    Review,
    High
}

public enum MatchedTitleVariant
{
    None,
    Original,
    English,
    Romaji
}

public sealed record ScoredCandidate
{
    public required JitenMatchCandidate Candidate { get; init; }
    public int TotalScore { get; init; }
    public int TitleScore { get; init; }
    public int VolumeScore { get; init; }
    public int CharacterCountScore { get; init; }
    public bool IsDisqualified { get; init; }
    public string? DisqualificationReason { get; init; }
    public MatchedTitleVariant MatchedTitle { get; init; }
    public bool IsPartialTitleMatch { get; init; }
    public StructuredVolume? CandidateVolume { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record JitenMatchResult
{
    public required ParsedMediaTitle ParsedTitle { get; init; }
    public MatchConfidence Confidence { get; init; }
    public int? AuthoritativeTtsuTotal { get; init; }
    public ScoredCandidate? BestCandidate => Candidates.Count > 0 ? Candidates[0] : null;
    public int RunnerUpMargin { get; init; }
    public IReadOnlyList<ScoredCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public int FilteredIncompatibleCount { get; init; } = 0;
}

