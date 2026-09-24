namespace Kiseki.Core.Entities;

public sealed class JitenCatalogueRefreshReceipt
{
    public Guid Id { get; set; }
    public Guid MediaSeriesId { get; set; }
    public int JitenDeckId { get; set; }
    public string ReviewFingerprint { get; set; } = string.Empty;
    public int AddedInstallments { get; set; }
    public int LinkedIdentities { get; set; }
    public int UpdatedInstallments { get; set; }
    public int MarkedMissing { get; set; }
    public int Ignored { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
