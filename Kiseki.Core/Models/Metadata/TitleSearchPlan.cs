namespace Kiseki.Core.Models.Metadata;

public enum SeriesQualifier
{
    Mainline,
    Ex,
    ShortStories,
    ArtBook,
    OtherSpecial
}

public enum VolumeInferenceKind
{
    None,
    Explicit,
    AttachedAsciiHypothesis,
    ImplicitFirstVolume
}

public sealed record TitleTransformation(
    string Kind,
    string Description,
    string Input,
    string Output);

public sealed record TitleSearchPlan
{
    public required string OriginalTitle { get; init; }
    public required string ComparisonTitle { get; init; }
    public required string CanonicalBaseTitle { get; init; }
    public StructuredVolume? Volume { get; init; }
    public SeriesQualifier SeriesQualifier { get; init; } = SeriesQualifier.Mainline;
    public VolumeInferenceKind VolumeInference { get; init; } = VolumeInferenceKind.None;
    public IReadOnlyList<TitleTransformation> Transformations { get; init; } = [];
    public IReadOnlyList<string> SearchAliases { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool HasVolume => Volume is not null;
    public bool IsExplicitVolume => VolumeInference == VolumeInferenceKind.Explicit;
    public bool IsTentativeVolume => VolumeInference is VolumeInferenceKind.AttachedAsciiHypothesis or VolumeInferenceKind.ImplicitFirstVolume;
}

