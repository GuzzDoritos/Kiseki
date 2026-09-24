namespace Kiseki.Core.Entities;

public enum InstallmentKind
{
    Unknown = 0,
    Volume = 1,
    Season = 2,
    Cour = 3,
    Movie = 4,
    Ova = 5,
    Special = 6,
    Game = 7
}

public enum ReleaseState
{
    Unknown = 0,
    Upcoming = 1,
    Released = 2
}

public enum CanonicalCoverSource
{
    None = 0,
    ProviderExact = 1,
    ProviderParentFallback = 2,
    LegacyUnknown = 3
}

public sealed class MediaInstallment
{
    private MediaInstallment() { }

    public MediaInstallment(string legacyTitle, MediaType mediaType, int orderKey = 100)
    {
        if (string.IsNullOrWhiteSpace(legacyTitle))
        {
            throw new ArgumentException("Installment title cannot be empty.", nameof(legacyTitle));
        }

        if (orderKey < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(orderKey), "Order key cannot be negative.");
        }

        LegacyTitle = legacyTitle.Trim();
        MediaType = mediaType;
        OrderKey = orderKey;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MediaSeriesId { get; set; }
    public MediaSeries? MediaSeries { get; set; }
    public MediaType MediaType { get; set; }
    public int OrderKey { get; set; }
    public InstallmentKind Kind { get; set; }

    // Provider values, manual corrections, and migrated unknown values remain separate.
    public string? LegacyTitle { get; set; }
    public string? CanonicalTitle { get; set; }
    public string? TitleOverride { get; set; }
    public ReleaseState ReleaseState { get; set; }
    public ReleaseState? ReleaseStateOverride { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public DateOnly? ReleaseDateOverride { get; set; }
    public bool IsIncluded { get; set; } = true;
    public int? CanonicalCharacterCount { get; set; }
    public int? CharacterCountOverride { get; set; }
    public string? CanonicalCoverUrl { get; set; }
    public CanonicalCoverSource CanonicalCoverSource { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<MediaWork> Copies { get; set; } = [];
    public List<InstallmentProviderIdentity> ProviderIdentities { get; set; } = [];

    public string DisplayTitle => TitleOverride ?? CanonicalTitle ?? LegacyTitle ?? string.Empty;
    public ReleaseState EffectiveReleaseState => ReleaseStateOverride ?? ReleaseState;
    public DateOnly? EffectiveReleaseDate => ReleaseDateOverride ?? ReleaseDate;
    public int? EffectiveCharacterCount => CharacterCountOverride ?? CanonicalCharacterCount;
}
