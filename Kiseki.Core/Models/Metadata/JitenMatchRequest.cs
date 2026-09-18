namespace Kiseki.Core.Models.Metadata;

public sealed record JitenMatchRequest
{
    public required Guid CorrelationId { get; init; }
    public required string RawTitle { get; init; }
    public int? AuthoritativeTtsuTotal { get; init; }
}

