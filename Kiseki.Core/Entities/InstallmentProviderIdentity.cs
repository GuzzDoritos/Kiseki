namespace Kiseki.Core.Entities;

public sealed class InstallmentProviderIdentity
{
    public const int MaxProviderLength = 64;
    public const int MaxNormalizedKeyLength = 256;

    public string Provider { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public Guid MediaInstallmentId { get; set; }
    public MediaInstallment MediaInstallment { get; set; } = null!;
    public int ProviderItemId { get; set; }
    public int? ParentProviderItemId { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public DateTimeOffset? MissingSinceUtc { get; set; }
    public List<InstallmentProviderSnapshot> Snapshots { get; set; } = [];
}
