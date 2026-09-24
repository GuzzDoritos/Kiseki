namespace Kiseki.Core.Entities;

public sealed class InstallmentProviderSnapshot
{
    public const int MaxFingerprintLength = 128;

    public string Provider { get; set; } = string.Empty;
    public string NormalizedKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public InstallmentProviderIdentity ProviderIdentity { get; set; } = null!;
    public string? Title { get; set; }
    public int? CharacterCount { get; set; }
    public string? CoverUrl { get; set; }
    public CanonicalCoverSource CoverSource { get; set; }
    public ReleaseState ReleaseState { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public int? ProviderOrder { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset? ObservedAtUtc { get; set; }
    public bool IsComplete { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
