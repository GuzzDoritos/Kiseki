namespace Kiseki.Core.Models.Metadata;

public enum VolumeKind
{
    Standard,
    Fractional,
    Position,
    Special
}

public enum PositionMarker
{
    Upper,  // 上
    Middle, // 中
    Lower   // 下
}

public sealed record StructuredVolume
{
    public VolumeKind Kind { get; init; }
    public decimal? Number { get; init; }
    public PositionMarker? Position { get; init; }
    public string? SpecialTag { get; init; }
    public bool IsSpecial { get; init; }
    public string RawMarker { get; init; } = string.Empty;

    public static StructuredVolume Standard(int number, string rawMarker = "") =>
        new()
        {
            Kind = VolumeKind.Standard,
            Number = number,
            IsSpecial = false,
            RawMarker = rawMarker
        };

    public static StructuredVolume Fractional(decimal number, string rawMarker = "") =>
        new()
        {
            Kind = VolumeKind.Fractional,
            Number = number,
            IsSpecial = true,
            RawMarker = rawMarker
        };

    public static StructuredVolume FromPosition(PositionMarker position, string rawMarker = "") =>
        new()
        {
            Kind = VolumeKind.Position,
            Position = position,
            IsSpecial = false,
            RawMarker = rawMarker
        };

    public static StructuredVolume Special(string tag, decimal? number = null, string rawMarker = "") =>
        new()
        {
            Kind = VolumeKind.Special,
            SpecialTag = tag,
            Number = number,
            IsSpecial = true,
            RawMarker = rawMarker
        };

    public bool Matches(StructuredVolume other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            VolumeKind.Standard => Number == other.Number,
            VolumeKind.Fractional => Number == other.Number,
            VolumeKind.Position => Position == other.Position,
            VolumeKind.Special => string.Equals(SpecialTag, other.SpecialTag, StringComparison.OrdinalIgnoreCase) &&
                                  Number == other.Number,
            _ => false
        };
    }

    public bool ConflictsWith(StructuredVolume other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !Matches(other);
    }

    public string FormatForEvidence() => Kind switch
    {
        VolumeKind.Standard => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Volume {Number}"),
        VolumeKind.Fractional => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Volume {Number}"),
        VolumeKind.Position => Position switch
        {
            PositionMarker.Upper => "Volume 上",
            PositionMarker.Middle => "Volume 中",
            PositionMarker.Lower => "Volume 下",
            _ => "Volume Position"
        },
        VolumeKind.Special => Number.HasValue
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SpecialTag} {Number}")
            : $"{SpecialTag}",
        _ => RawMarker
    };
}
