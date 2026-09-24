namespace Kiseki.Core.Entities;

/// <summary>
/// A durable user decision for one node in a Jiten franchise graph.  It never
/// represents a provider assertion that graph connectivity makes two series
/// interchangeable.
/// </summary>
public sealed class JitenFranchiseGraphNodeState
{
    public Guid FranchiseId { get; set; }
    public int DeckId { get; set; }
    public JitenFranchiseNodeResolution Resolution { get; set; }
    public Guid? MediaSeriesId { get; set; }
    public string LastProviderTitle { get; set; } = string.Empty;
    public int ProviderMediaType { get; set; }
    public string ProviderFingerprint { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public Franchise? Franchise { get; set; }
    public MediaSeries? MediaSeries { get; set; }
}

public enum JitenFranchiseNodeResolution
{
    Ignored = 1,
    Unresolved = 2,
    LinkedSeries = 3
}
