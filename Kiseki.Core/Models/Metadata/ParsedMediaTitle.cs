namespace Kiseki.Core.Models.Metadata;

public sealed record ParsedMediaTitle
{
    public required string OriginalTitle { get; init; }
    public required string ComparisonTitle { get; init; }
    public required string BaseTitle { get; init; }
    public StructuredVolume? Volume { get; init; }
    public bool HasVolume => Volume is not null;
    public bool IsSpecialVolume => Volume?.IsSpecial ?? false;
    public IReadOnlyList<string> ParsingNotes { get; init; } = [];
    public TitleSearchPlan? SearchPlan { get; init; }
    public SeriesQualifier SeriesQualifier => SearchPlan?.SeriesQualifier ?? SeriesQualifier.Mainline;
    public VolumeInferenceKind VolumeInference => SearchPlan?.VolumeInference ?? (Volume is not null ? VolumeInferenceKind.Explicit : VolumeInferenceKind.None);
}

