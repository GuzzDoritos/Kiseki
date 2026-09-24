using Kiseki.Core.Entities;
using Kiseki.Core.Services;

namespace Kiseki.Web.Models;

public sealed record FranchiseIndexItemViewModel(
    Guid Id,
    string Title,
    int? JitenAnchorDeckId,
    int SeriesCount,
    int BookSeriesCount,
    int AnimeSeriesCount,
    int OtherSeriesCount);

public sealed record FranchiseSeriesItemViewModel(
    Guid SeriesId,
    string Title,
    MediaType MediaType,
    int? JitenDeckId,
    FranchiseProgressUnit ProgressUnit,
    SeriesProgressResult? BookProgress,
    long LifetimeCharactersRead,
    double LifetimeMinutes)
{
    public string HeadlineProgressText =>
        ProgressUnit == FranchiseProgressUnit.Characters && BookProgress?.ProgressPercentage is double pct
            ? $"{pct:0.#}%"
            : "Not available";

    public ProgressBarViewModel? ProgressBar =>
        ProgressUnit == FranchiseProgressUnit.Characters && BookProgress?.ProgressPercentage is double pct
            ? new ProgressBarViewModel(
                Current: (int)Math.Round(pct),
                Total: 100,
                Label: $"{pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%",
                IsComplete: pct >= 100d,
                ShowPercentage: true,
                PercentageOverride: pct)
            : null;
}

public sealed record FranchiseDetailsViewModel(
    Guid Id,
    string Title,
    int? JitenAnchorDeckId,
    IReadOnlyList<FranchiseSeriesItemViewModel> Series,
    bool IsTruncated)
{
    public IEnumerable<FranchiseSeriesItemViewModel> BookSeries =>
        Series.Where(s => s.MediaType == MediaType.Book);

    public IEnumerable<FranchiseSeriesItemViewModel> AnimeSeries =>
        Series.Where(s => s.MediaType == MediaType.Anime);

    public IEnumerable<FranchiseSeriesItemViewModel> OtherSeries =>
        Series.Where(s => s.MediaType != MediaType.Book && s.MediaType != MediaType.Anime);
}

public sealed record AvailableSeriesOption(
    Guid SeriesId,
    string Title,
    MediaType MediaType,
    string? CurrentFranchiseTitle);

public sealed record CandidateSeriesOption(
    Guid SeriesId,
    string Title);

public sealed record FranchiseTopologyProposalViewModel(
    string ProposalId,
    int DeckId,
    string Title,
    int ProviderMediaType,
    JitenFranchiseNodeClassification Classification,
    int CharacterCount,
    int ChildrenDeckCount,
    JitenFranchiseNodeProposalKind Kind,
    Guid? ExactSeriesId,
    string? ExactSeriesTitle,
    IReadOnlyList<CandidateSeriesOption> CandidateSeries,
    IReadOnlyList<JitenFranchiseNodeAction> AllowedActions,
    JitenFranchiseNodeAction RecommendedAction,
    JitenFranchiseNodeResolution? PersistedResolution,
    string Message,
    JitenFranchiseNodeAction SelectedAction,
    Guid? SelectedSeriesId);

public sealed record FranchiseTopologyReviewViewModel(
    Guid ReviewId,
    Guid FranchiseId,
    int AnchorDeckId,
    string Fingerprint,
    bool IsCompleteGraph,
    bool IsTruncated,
    string? Warning,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<FranchiseTopologyProposalViewModel> Proposals,
    Guid OperationId);

public sealed class FranchiseTopologyChoiceInput
{
    public string ProposalId { get; set; } = string.Empty;
    public JitenFranchiseNodeAction Action { get; set; }
    public Guid? MediaSeriesId { get; set; }
}

public sealed record FranchiseTopologyReceiptViewModel(
    Guid OperationId,
    Guid FranchiseId,
    int AnchorDeckId,
    int CreatedSeries,
    int LinkedSeries,
    int IgnoredNodes,
    int UnresolvedNodes,
    DateTimeOffset CompletedAtUtc,
    bool IsReplayed);
