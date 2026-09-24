using System.ComponentModel.DataAnnotations;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;

namespace Kiseki.Web.Models;

public enum RefreshJitenUiState
{
    DeckPrompt,
    Reviewing,
    Receipt,
    Error
}

public sealed record CandidateInstallmentOption(
    Guid Id,
    string Title,
    int OrderKey);

public sealed record JitenReviewProposalViewModel(
    string ProposalId,
    string ProviderKey,
    JitenCatalogueProposalKind Kind,
    string Title,
    int? CharacterCount,
    string? CoverUrl,
    CanonicalCoverSource CoverSource,
    ReleaseState ReleaseState,
    DateOnly? ReleaseDate,
    int ProviderOrder,
    Guid? ExistingInstallmentId,
    string? ExistingInstallmentTitle,
    IReadOnlyList<CandidateInstallmentOption> Candidates,
    IReadOnlyList<JitenCatalogueChoiceAction> AllowedActions,
    JitenCatalogueChoiceAction RecommendedAction,
    JitenCatalogueChoiceAction SelectedAction,
    Guid? SelectedInstallmentId,
    bool HasManualConflict,
    bool HasUncertainOrder,
    string Message)
{
    public string BadgeText => Kind switch
    {
        JitenCatalogueProposalKind.ExactIdentity => "Exact Match",
        JitenCatalogueProposalKind.Addition => "New Volume",
        JitenCatalogueProposalKind.AmbiguousAssociation => "Ambiguous Association",
        JitenCatalogueProposalKind.DuplicateProviderIdentity => "Duplicate Identity",
        JitenCatalogueProposalKind.MissingProviderEntry => "Disappeared From Jiten",
        JitenCatalogueProposalKind.AbsenceUnverified => "Absence Unverified",
        _ => Kind.ToString()
    };

    public string BadgeCssClass => Kind switch
    {
        JitenCatalogueProposalKind.ExactIdentity => "badge-exact",
        JitenCatalogueProposalKind.Addition => "badge-addition",
        JitenCatalogueProposalKind.AmbiguousAssociation => "badge-ambiguous",
        JitenCatalogueProposalKind.DuplicateProviderIdentity => "badge-duplicate",
        JitenCatalogueProposalKind.MissingProviderEntry => "badge-missing",
        JitenCatalogueProposalKind.AbsenceUnverified => "badge-unverified",
        _ => "badge-secondary"
    };

    public string ReleaseStateText => ReleaseState switch
    {
        ReleaseState.Released => ReleaseDate.HasValue ? $"Released ({ReleaseDate:yyyy-MM-dd})" : "Released",
        ReleaseState.Upcoming => ReleaseDate.HasValue ? $"Upcoming ({ReleaseDate:yyyy-MM-dd})" : "Upcoming",
        _ => "Unknown release"
    };
}

public sealed record JitenRefreshReceiptViewModel(
    Guid OperationId,
    Guid MediaSeriesId,
    int JitenDeckId,
    int AddedInstallments,
    int LinkedIdentities,
    int UpdatedInstallments,
    int MarkedMissing,
    int Ignored,
    DateTimeOffset CompletedAtUtc,
    bool IsReplayed);

public sealed record SeriesRefreshJitenViewModel(
    Guid SeriesId,
    string Title,
    MediaType MediaType,
    int? JitenDeckId,
    RefreshJitenUiState State,
    Guid? ReviewId = null,
    string? Fingerprint = null,
    Guid? OperationId = null,
    DateTimeOffset? ExpiresAtUtc = null,
    bool IsCompleteFetch = true,
    int ExpectedItems = 0,
    int RetrievedItems = 0,
    string? Warning = null,
    IReadOnlyList<JitenReviewProposalViewModel>? Proposals = null,
    JitenRefreshReceiptViewModel? Receipt = null,
    string? ErrorMessage = null,
    int? RetryAfterSeconds = null,
    bool IsStaleReview = false)
{
    public string TypeToJapanese => MediaType switch
    {
        MediaType.Book => "本",
        MediaType.Anime => "アニメ",
        MediaType.Game => "ゲーム",
        _ => "本"
    };
}

public sealed class ProposalChoiceInput
{
    [Required]
    public string ProposalId { get; set; } = string.Empty;

    [Required]
    public JitenCatalogueChoiceAction Action { get; set; }

    public Guid? InstallmentId { get; set; }
}

