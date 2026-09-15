namespace Kiseki.Core.Entities;

// One TTSU history per work. The work ID also identifies its binding.
public sealed class TtsuBinding
{
    public Guid MediaWorkId { get; set; }
    public string OriginalTitle { get; set; } = string.Empty;
    public string? FolderHint { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public int? CurrentCharacterPosition { get; set; }
    public double? ProgressFraction { get; set; }
    public long? ProgressRevision { get; set; }
    public int? ProgressExporterVersion { get; set; }
    public int? ProgressDatabaseVersion { get; set; }
    public TtsuTotalInferenceKind? TotalInferenceKind { get; set; }
}

public enum TtsuTotalInferenceKind
{
    ExactRatio,
    CompletionAdjusted,
    RoundedPercentage
}

public sealed class TtsuImportReceipt
{
    public Guid Id { get; set; }
    public int Books { get; set; }
    public int AddedDays { get; set; }
    public int UpdatedDays { get; set; }
    public int UnchangedDays { get; set; }
    public int StaleDays { get; set; }
    public int ProgressUpdates { get; set; }
    public int CharacterTotalUpdates { get; set; }
}
