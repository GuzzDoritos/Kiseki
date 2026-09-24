namespace Kiseki.Core.Entities;

/// <summary>Durable replay result for a reviewed Jiten franchise topology application.</summary>
public sealed class JitenFranchiseTopologyReceipt
{
    public Guid Id { get; set; }
    public Guid FranchiseId { get; set; }
    public int AnchorDeckId { get; set; }
    public string ReviewFingerprint { get; set; } = string.Empty;
    public int CreatedSeries { get; set; }
    public int LinkedSeries { get; set; }
    public int IgnoredNodes { get; set; }
    public int UnresolvedNodes { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
