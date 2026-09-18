using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Web.Models;

public enum TtsuEnrichmentBadge
{
    AutoMatched,
    NeedsReview,
    NoSafeMatch,
    JitenUnavailable
}

public sealed record TtsuCandidateChoiceViewModel(
    Guid Key,
    int DeckId,
    int? SubdeckId,
    string DisplayTitle,
    string RomajiTitle,
    string EnglishTitle,
    int CharacterCount,
    int Score,
    string? CoverUrl,
    JitenCoverEvidence CoverEvidence,
    string CoverEvidenceLabel,
    IReadOnlyList<string> Evidence,
    bool IsTopCandidate,
    bool IsSelectable);

public sealed record TtsuBookEnrichmentViewModel(
    TtsuEnrichmentBadge Badge,
    string BadgeLabel,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TtsuCandidateChoiceViewModel> Candidates,
    Guid? SelectedCandidateKey,
    bool IsMetadataApplicationEligible,
    string? IneligibilityReason);

public sealed record TtsuBookPreviewViewModel(
    Guid BookKey,
    string Title,
    string? FolderHint,
    string MatchReason,
    TtsuImportPlan Plan,
    TtsuBookEnrichmentViewModel? Enrichment = null)
{
    public Guid? ExistingMediaWorkId => Plan.TargetId;
    public bool ExistsInLibrary => ExistingMediaWorkId.HasValue;
    public long CharactersRead => Plan.ResultCharacters;
    public int? CurrentPosition => Plan.Progress.Accepted?.CharacterPosition ?? Plan.Progress.Existing?.CharacterPosition;
    public double? PositionPercentage => (Plan.Progress.Accepted?.ProgressFraction ?? Plan.Progress.Existing?.ProgressFraction) * 100d;
    public static string FormatDuration(double seconds)
    {
        var minutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        return minutes >= 60 ? $"{minutes / 60:N0}h {minutes % 60}m" : $"{minutes}m";
    }
}

public sealed record TtsuOrphanViewModel(Guid Id, DateOnly Date, int Characters, double Minutes);

public sealed class TtsuBookSelectionInput
{
    public Guid BookKey { get; set; }
    public bool Selected { get; set; }
    public TtsuImportMode Mode { get; set; }
    public Guid? TargetId { get; set; }
    public Guid ReviewToken { get; set; }
    public List<TtsuDayResolutionInput> Days { get; set; } = [];
    public List<Guid> OrphanLogIds { get; set; } = [];
    public string ProgressChoice { get; set; } = string.Empty;
    public Guid? CandidateKey { get; set; }
}

public sealed class TtsuDayResolutionInput
{
    public DateOnly Date { get; set; }
    public string Choice { get; set; } = string.Empty;
}

public enum TtsuImportMode { Merge, Create }
