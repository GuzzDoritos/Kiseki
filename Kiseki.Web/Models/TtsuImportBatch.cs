using Kiseki.Core.DTOs;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Web.Models;

public enum TtsuEnrichmentAttemptState
{
    NotRequested,
    Pending,
    InProgress,
    Completed
}

public sealed record TtsuImportBatch(
    Guid Id,
    IReadOnlyList<TtsuImportBatchBook> Books)
{
    public object SyncLock { get; } = new();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, TtsuReviewedPlan> Reviews { get; } = new();

    public bool TryClaimNextPendingBook(
        out TtsuImportBatchBook? claimedBook,
        out int completedCount,
        out int totalCount,
        out int currentNumber)
    {
        lock (SyncLock)
        {
            totalCount = Books.Count;
            completedCount = Books.Count(b => b.EnrichmentState == TtsuEnrichmentAttemptState.Completed);

            if (completedCount == totalCount)
            {
                claimedBook = null;
                currentNumber = totalCount;
                return true;
            }

            var pending = Books.FirstOrDefault(b => b.EnrichmentState == TtsuEnrichmentAttemptState.Pending);
            if (pending is not null)
            {
                pending.EnrichmentState = TtsuEnrichmentAttemptState.InProgress;
                claimedBook = pending;
                currentNumber = completedCount + 1;
                return true;
            }

            // All remaining unfinished items are currently in progress by other requests
            claimedBook = null;
            currentNumber = Math.Min(totalCount, completedCount + 1);
            return false;
        }
    }
}

public sealed record TtsuReviewedPlan(Guid BookKey, string Fingerprint, Guid? CandidateKey = null, string? SelectedCoverKey = null);

public sealed record TtsuCandidateGoogleCover(
    string VolumeId,
    string CoverUrl,
    string? AttributionLink,
    IReadOnlyList<string> Evidence,
    GoogleBooksIdentityProof Proof = GoogleBooksIdentityProof.ExplicitVolume,
    bool IsLowResolution = false,
    Kiseki.Core.Models.Covers.ExternalCoverProvider Provider = Kiseki.Core.Models.Covers.ExternalCoverProvider.GoogleBooks,
    int? Width = null,
    int? Height = null,
    string? NormalizedIsbn = null);

public sealed record TtsuEnrichmentCandidate(
    Guid Key,
    JitenMatchCandidate Candidate,
    int Score,
    IReadOnlyList<string> Evidence,
    bool IsTopCandidate,
    bool IsSelectable,
    TtsuCandidateGoogleCover? GoogleCover = null,
    GoogleBooksMatchStatus? GoogleCoverStatus = null,
    string? GoogleCoverWarning = null,
    bool IsDisqualified = false,
    string? DisqualificationReason = null,
    bool IsExactIdentity = false,
    IReadOnlyList<Kiseki.Core.Models.Covers.CoverEditionOption>? CoverOptions = null);

public sealed record TtsuBookEnrichment(
    JitenMatchStatus Status,
    MatchConfidence Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TtsuEnrichmentCandidate> Candidates,
    int OmittedPlausibleCount = 0,
    int FilteredIncompatibleCount = 0);

public sealed record TtsuImportBatchBook(
    Guid Key,
    TtsuBookContainer Book)
{
    public TtsuEnrichmentAttemptState EnrichmentState { get; set; } = TtsuEnrichmentAttemptState.NotRequested;
    public TtsuBookEnrichment? Enrichment { get; set; }
}
