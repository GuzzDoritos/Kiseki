namespace Kiseki.Core.Models.Metadata;

public enum JitenMatchStatus
{
    Matched,
    NoCandidates,
    Unavailable,
    RateLimited
}

public sealed record JitenMatchOutcome
{
    public required Guid CorrelationId { get; init; }
    public required JitenMatchStatus Status { get; init; }
    public JitenMatchResult? Result { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? MatchedAlias { get; init; }

    public static JitenMatchOutcome Matched(
        Guid correlationId,
        JitenMatchResult result,
        IReadOnlyList<string>? warnings = null,
        string? matchedAlias = null) =>
        new()
        {
            CorrelationId = correlationId,
            Status = JitenMatchStatus.Matched,
            Result = result,
            Warnings = warnings ?? [],
            MatchedAlias = matchedAlias
        };

    public static JitenMatchOutcome NoCandidates(
        Guid correlationId,
        JitenMatchResult? result = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            CorrelationId = correlationId,
            Status = JitenMatchStatus.NoCandidates,
            Result = result,
            Warnings = warnings ?? []
        };

    public static JitenMatchOutcome Unavailable(
        Guid correlationId,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            CorrelationId = correlationId,
            Status = JitenMatchStatus.Unavailable,
            Result = null,
            Warnings = warnings ?? []
        };

    public static JitenMatchOutcome RateLimited(
        Guid correlationId,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            CorrelationId = correlationId,
            Status = JitenMatchStatus.RateLimited,
            Result = null,
            Warnings = warnings ?? []
        };
}

