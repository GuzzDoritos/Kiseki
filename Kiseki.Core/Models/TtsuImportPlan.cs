namespace Kiseki.Core.Models;

public enum TtsuDayAction { Added, Updated, Unchanged, Stale, Conflict }
public enum TtsuProgressAction { None, Added, Updated, Unchanged, Stale, Conflict }
public enum TtsuCopyIntent { ExistingCopy, NewCopyUnderExistingInstallment, NewInstallmentAndCopy }
public sealed record TtsuDailySnapshot(DateOnly Date, int Characters, double Minutes, long? Revision);
public sealed record TtsuStoredDay(Guid Id, int Characters, double Minutes, long? Revision);
public sealed record TtsuProgressSnapshot(
    int CharacterPosition,
    double ProgressFraction,
    long? Revision,
    int? InferredTotalCharacters,
    Entities.TtsuTotalInferenceKind InferenceKind,
    int? ExporterVersion,
    int? DatabaseVersion);
public sealed record TtsuProgressPlan(
    TtsuProgressSnapshot? Existing,
    IReadOnlyList<TtsuProgressSnapshot> Incoming,
    TtsuProgressAction Action,
    TtsuProgressSnapshot? Accepted,
    string? ReviewReason)
{
    public bool RequiresReview => Action == TtsuProgressAction.Conflict;
    public int? ResultingTotalCharacters => Accepted?.InferredTotalCharacters ?? Existing?.InferredTotalCharacters;
}
public sealed record TtsuDayPlan(
    DateOnly Date, IReadOnlyList<TtsuStoredDay> Existing, IReadOnlyList<TtsuDailySnapshot> Incoming,
    TtsuDayAction Action, TtsuDailySnapshot? Accepted, Guid? RetainedLogId, string? ReviewReason)
{
    public long CharacterDelta => (Accepted?.Characters ?? Existing.SingleOrDefault(x => x.Id == RetainedLogId)?.Characters ?? Existing.Sum(x => (long)x.Characters)) - Existing.Sum(x => (long)x.Characters);
    public double MinuteDelta => (Accepted?.Minutes ?? Existing.SingleOrDefault(x => x.Id == RetainedLogId)?.Minutes ?? Existing.Sum(x => x.Minutes)) - Existing.Sum(x => x.Minutes);
}
public sealed record TtsuImportPlan(
    Guid? TargetId, string TargetTitle, string Fingerprint, IReadOnlyList<TtsuDayPlan> Days,
    long CurrentCharacters, double CurrentMinutes, string? Error, TtsuProgressPlan Progress,
    long AssignedCharacters = 0, double AssignedMinutes = 0,
    TtsuImportTargetReview? TargetReview = null)
{
    public bool CanApply => Error is null && Days.All(day => day.Action != TtsuDayAction.Conflict) && !Progress.RequiresReview;
    public long ResultCharacters => CurrentCharacters + AssignedCharacters + Days.Sum(day => day.CharacterDelta);
    public double ResultMinutes => CurrentMinutes + AssignedMinutes + Days.Sum(day => day.MinuteDelta);
    public int Count(TtsuDayAction action) => Days.Count(day => day.Action == action);
}
public sealed record TtsuMatch(Guid? WorkId, string Reason, bool IsAmbiguous = false);
public sealed record TtsuTarget(Guid Id, string Title);
public sealed record TtsuInstallmentTarget(Guid Id, string Title, Guid Version, Guid? SeriesId, int CopyCount);

public sealed record TtsuProviderIdentityHint(string Provider, string NormalizedKey)
{
    public static TtsuProviderIdentityHint FromJiten(JitenMediaSelection selection) => new(
        "jiten",
        selection.SubdeckId is int child
            ? $"subdeck:{selection.DeckId}:{child}"
            : $"deck:{selection.DeckId}");
}

public sealed record TtsuImportTargetChoice(
    TtsuCopyIntent Intent,
    Guid? WorkId = null,
    Guid? InstallmentId = null)
{
    public static TtsuImportTargetChoice ExistingCopy(Guid workId) =>
        new(TtsuCopyIntent.ExistingCopy, WorkId: workId);

    public static TtsuImportTargetChoice NewCopy(Guid installmentId) =>
        new(TtsuCopyIntent.NewCopyUnderExistingInstallment, InstallmentId: installmentId);

    public static TtsuImportTargetChoice NewInstallment() =>
        new(TtsuCopyIntent.NewInstallmentAndCopy);
}

public sealed record TtsuCopyTargetCandidate(
    Guid WorkId,
    string Title,
    Guid WorkVersion,
    Guid? InstallmentId,
    Guid? InstallmentVersion,
    Guid? SeriesId,
    bool HasBinding,
    bool BindingMatchesSource,
    Guid? BindingVersion,
    long LifetimeCharacters);

public sealed record TtsuInstallmentTargetCandidate(
    Guid InstallmentId,
    string Title,
    Guid InstallmentVersion,
    Guid? SeriesId,
    int CopyCount,
    int UnboundCopyCount,
    IReadOnlyList<string> ProviderKeys);

public sealed record TtsuImportTargetReview(
    TtsuImportTargetChoice? SuggestedChoice,
    string Reason,
    bool IsAmbiguous,
    IReadOnlyList<TtsuCopyTargetCandidate> CopyCandidates,
    IReadOnlyList<TtsuInstallmentTargetCandidate> InstallmentCandidates,
    TtsuProviderIdentityHint? ProviderIdentity = null);
public sealed record TtsuMetadataImportRequest(
    JitenMediaSelection? Selection,
    GoogleBooks.GoogleBooksCoverSelection? GoogleCover = null,
    Covers.ExternalCoverSelection? ExternalCover = null)
{
    public Covers.ExternalCoverSelection? EffectiveCover =>
        ExternalCover ?? (GoogleCover is not null
            ? new Covers.ExternalCoverSelection(
                Covers.ExternalCoverProvider.GoogleBooks,
                GoogleCover.CoverUrl,
                GoogleCover.VolumeId,
                GoogleCover.AttributionLink)
            : null);
}

public sealed record TtsuImportRequest(
    DTOs.TtsuBookContainer Book,
    Guid? TargetId,
    IReadOnlyDictionary<DateOnly, string> Resolutions,
    string ExpectedFingerprint,
    IReadOnlyList<Guid>? OrphanLogIds = null,
    string? ProgressResolution = null,
    TtsuMetadataImportRequest? Metadata = null,
    TtsuImportTargetChoice? TargetChoice = null,
    TtsuProviderIdentityHint? ProviderIdentity = null);

public sealed class TtsuImportReviewRequiredException(string message) : Exception(message);
