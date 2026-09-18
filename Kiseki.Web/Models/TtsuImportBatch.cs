using Kiseki.Core.DTOs;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Web.Models;

public sealed record TtsuImportBatch(
    Guid Id,
    IReadOnlyList<TtsuImportBatchBook> Books)
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, TtsuReviewedPlan> Reviews { get; } = new();
}

public sealed record TtsuReviewedPlan(Guid BookKey, string Fingerprint, Guid? CandidateKey = null);

public sealed record TtsuEnrichmentCandidate(
    Guid Key,
    JitenMatchCandidate Candidate,
    int Score,
    IReadOnlyList<string> Evidence,
    bool IsTopCandidate,
    bool IsSelectable);

public sealed record TtsuBookEnrichment(
    JitenMatchStatus Status,
    MatchConfidence Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TtsuEnrichmentCandidate> Candidates);

public sealed record TtsuImportBatchBook(
    Guid Key,
    TtsuBookContainer Book)
{
    public TtsuBookEnrichment? Enrichment { get; set; }
}
