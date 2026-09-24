using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core.Services;

public enum JitenFranchiseNodeClassification
{
    Unknown = 0,
    Book = 1
}

public enum JitenFranchiseNodeProposalKind
{
    ExactSeriesIdentity,
    CandidateSeries,
    NewBookSeries,
    UnsupportedMedia,
    IgnoredDecision,
    DuplicateProviderNode
}

public enum JitenFranchiseNodeAction
{
    LinkExistingSeries,
    CreateSeparateBookSeries,
    Ignore,
    KeepUnresolved
}

public sealed record JitenFranchiseProviderNode(int DeckId, string Title, int ProviderMediaType,
    JitenFranchiseNodeClassification Classification, int CharacterCount, int ChildrenDeckCount,
    string Fingerprint);

public sealed record JitenFranchiseProviderEdge(int SourceDeckId, int TargetDeckId, int RelationshipType);

public sealed record JitenFranchiseNodeProposal(string ProposalId, JitenFranchiseProviderNode Node,
    JitenFranchiseNodeProposalKind Kind, Guid? ExactSeriesId, IReadOnlyList<Guid> CandidateSeriesIds,
    IReadOnlyList<JitenFranchiseNodeAction> AllowedActions, JitenFranchiseNodeAction RecommendedAction,
    JitenFranchiseNodeResolution? PersistedResolution, string Message);

public sealed record JitenFranchiseTopologyChoice(string ProposalId, JitenFranchiseNodeAction Action,
    Guid? MediaSeriesId = null);

public sealed record JitenFranchiseTopologyReview(Guid ReviewId, Guid FranchiseId, int AnchorDeckId,
    string Fingerprint, string ProviderFingerprint, string LocalFingerprint, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, bool IsCompleteGraph, bool IsTruncated, string? Warning,
    IReadOnlyList<JitenFranchiseProviderNode> Nodes, IReadOnlyList<JitenFranchiseProviderEdge> Edges,
    IReadOnlyList<JitenFranchiseNodeProposal> Proposals,
    IReadOnlyList<JitenFranchiseTopologyChoice>? ApprovedChoices = null,
    string? ApprovalFingerprint = null);

public sealed record JitenFranchiseTopologyReceiptResult(Guid OperationId, Guid FranchiseId, int AnchorDeckId,
    int CreatedSeries, int LinkedSeries, int IgnoredNodes, int UnresolvedNodes, DateTimeOffset CompletedAtUtc);

public interface IJitenFranchiseTopologyReviewStore
{
    void Save(JitenFranchiseTopologyReview review);
    JitenFranchiseTopologyReview? Get(Guid reviewId, DateTimeOffset now);
    bool TryApprove(Guid reviewId, string expectedFingerprint, IReadOnlyList<JitenFranchiseTopologyChoice> choices,
        string approvalFingerprint, DateTimeOffset now, out JitenFranchiseTopologyReview? approvedReview);
}

public sealed class InMemoryJitenFranchiseTopologyReviewStore : IJitenFranchiseTopologyReviewStore
{
    private const int MaxReviews = 128;
    private readonly ConcurrentDictionary<Guid, JitenFranchiseTopologyReview> _reviews = new();

    public void Save(JitenFranchiseTopologyReview review)
    {
        foreach (var expired in _reviews.Where(item => item.Value.ExpiresAtUtc <= review.CreatedAtUtc))
            _reviews.TryRemove(expired.Key, out _);
        _reviews[review.ReviewId] = review;
        foreach (var oldest in _reviews.OrderBy(item => item.Value.CreatedAtUtc)
                     .Take(Math.Max(0, _reviews.Count - MaxReviews)))
            _reviews.TryRemove(oldest.Key, out _);
    }

    public JitenFranchiseTopologyReview? Get(Guid reviewId, DateTimeOffset now)
    {
        if (!_reviews.TryGetValue(reviewId, out var review)) return null;
        if (review.ExpiresAtUtc > now) return review;
        _reviews.TryRemove(reviewId, out _);
        return null;
    }

    public bool TryApprove(Guid reviewId, string expectedFingerprint, IReadOnlyList<JitenFranchiseTopologyChoice> choices,
        string approvalFingerprint, DateTimeOffset now, out JitenFranchiseTopologyReview? approvedReview)
    {
        approvedReview = null;
        while (_reviews.TryGetValue(reviewId, out var current))
        {
            if (current.ExpiresAtUtc <= now || current.Fingerprint != expectedFingerprint)
            {
                if (current.ExpiresAtUtc <= now) _reviews.TryRemove(reviewId, out _);
                return false;
            }

            var replacement = current with
            {
                ApprovedChoices = choices.ToArray(),
                ApprovalFingerprint = approvalFingerprint
            };
            if (_reviews.TryUpdate(reviewId, replacement, current))
            {
                approvedReview = replacement;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Fetches Jiten graph evidence and turns it into explicit node-level choices.
/// Edges are deliberately not converted into a progression order or a series
/// merge instruction.
/// </summary>
public sealed class JitenFranchiseTopologyService
{
    private const int MaxNodes = 250;
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(20);
    private readonly ImmersionDbContext _context;
    private readonly IJitenApiClient _client;
    private readonly IJitenFranchiseTopologyReviewStore _store;
    private readonly TimeProvider _timeProvider;

    public JitenFranchiseTopologyService(ImmersionDbContext context, IJitenApiClient client,
        IJitenFranchiseTopologyReviewStore store, TimeProvider? timeProvider = null)
    {
        _context = context;
        _client = client;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<JitenFranchiseTopologyReview> PreviewAsync(Guid franchiseId, int anchorDeckId,
        CancellationToken cancellationToken = default)
    {
        if (franchiseId == Guid.Empty || anchorDeckId <= 0)
            throw new JitenFranchiseTopologyReviewRequiredException("Choose a valid franchise and Jiten anchor deck.");

        // Fetch before database work: network delays must never hold a write transaction.
        var graph = await _client.GetFranchiseAsync(anchorDeckId, cancellationToken)
            ?? throw new JitenFranchiseTopologyReviewRequiredException("Jiten could not find the requested franchise graph.");
        var normalized = NormalizeGraph(graph);
        var local = await LoadLocalAsync(franchiseId, cancellationToken);
        var providerFingerprint = FingerprintProvider(anchorDeckId, graph, normalized);
        var localFingerprint = FingerprintLocal(local);
        var proposals = JitenFranchiseTopologyPlanner.Plan(normalized.Nodes, local.Series, local.States,
            normalized.IsCompleteGraph);
        var now = _timeProvider.GetUtcNow();
        var fingerprint = Hash(new
        {
            franchiseId,
            anchorDeckId,
            providerFingerprint,
            localFingerprint,
            normalized.IsCompleteGraph,
            proposals = proposals.Select(ProposalShape)
        });
        var review = new JitenFranchiseTopologyReview(Guid.NewGuid(), franchiseId, anchorDeckId, fingerprint,
            providerFingerprint, localFingerprint, now, now + ReviewLifetime, normalized.IsCompleteGraph,
            graph.Truncated, normalized.Warning, normalized.Nodes, normalized.Edges, proposals);
        _store.Save(review);
        return review;
    }

    public JitenFranchiseTopologyReview Approve(Guid reviewId, string expectedFingerprint,
        IReadOnlyList<JitenFranchiseTopologyChoice> choices)
    {
        var now = _timeProvider.GetUtcNow();
        var review = _store.Get(reviewId, now)
            ?? throw new JitenFranchiseTopologyReviewRequiredException("The topology review expired. Fetch and review Jiten again.");
        if (review.Fingerprint != expectedFingerprint)
            throw new JitenFranchiseTopologyReviewRequiredException("The graph evidence changed. Review it again before confirming.");
        ValidateChoices(review, choices);
        var normalized = choices.OrderBy(choice => choice.ProposalId, StringComparer.Ordinal).ToArray();
        var approvalFingerprint = Hash(new { review.Fingerprint, Choices = normalized });
        if (!_store.TryApprove(reviewId, expectedFingerprint, normalized, approvalFingerprint, now, out var approved) || approved is null)
            throw new JitenFranchiseTopologyReviewRequiredException("The topology review changed or expired. Review it again.");
        return approved;
    }

    public JitenFranchiseTopologyReview? GetActiveReview(Guid reviewId) =>
        reviewId == Guid.Empty ? null : _store.Get(reviewId, _timeProvider.GetUtcNow());

    public Task<JitenFranchiseTopologyReceipt?> GetReceiptAsync(Guid operationId, Guid franchiseId,
        CancellationToken cancellationToken = default) =>
        _context.JitenFranchiseTopologyReceipts.AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.Id == operationId && receipt.FranchiseId == franchiseId,
                cancellationToken);

    public async Task<JitenFranchiseTopologyReceiptResult> ApplyAsync(Guid operationId, Guid franchiseId,
        Guid reviewId, string approvalFingerprint, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || franchiseId == Guid.Empty || reviewId == Guid.Empty ||
            string.IsNullOrWhiteSpace(approvalFingerprint))
            throw new JitenFranchiseTopologyReviewRequiredException(
                "A valid approved topology review and operation ID are required.", canRetryCurrentReview: true);

        var replay = await GetReceiptAsync(operationId, franchiseId, cancellationToken);
        if (replay is not null) return ToResult(replay);

        var now = _timeProvider.GetUtcNow();
        var review = _store.Get(reviewId, now)
            ?? throw new JitenFranchiseTopologyReviewRequiredException("The topology review expired. Fetch and review Jiten again.");
        if (review.FranchiseId != franchiseId || review.ApprovedChoices is null ||
            review.ApprovalFingerprint != approvalFingerprint)
            throw new JitenFranchiseTopologyReviewRequiredException(
                "Approve the current server-held topology review before applying it.");

        var freshGraph = await _client.GetFranchiseAsync(review.AnchorDeckId, cancellationToken)
            ?? throw new JitenFranchiseTopologyReviewRequiredException("Jiten could not find the requested franchise graph.");
        var freshNormalized = NormalizeGraph(freshGraph);
        if (FingerprintProvider(review.AnchorDeckId, freshGraph, freshNormalized) != review.ProviderFingerprint)
            throw new JitenFranchiseTopologyReviewRequiredException(
                "Jiten changed since the preview. Review the refreshed graph before confirming.");

        try
        {
            return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                var existing = await GetReceiptAsync(operationId, franchiseId, cancellationToken);
                if (existing is not null) return ToResult(existing);

                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var local = await LoadTrackedAsync(franchiseId, cancellationToken);
                if (FingerprintLocal(local) != review.LocalFingerprint)
                    throw new JitenFranchiseTopologyReviewRequiredException(
                        "The local franchise changed since the preview. Review the graph again before applying it.");

                var receipt = await ApplyReviewedChoicesAsync(operationId, review, local, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToResult(receipt);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new JitenFranchiseTopologyReviewRequiredException(
                "The franchise changed while the topology was being saved. Review it again.");
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            var receipt = await GetReceiptAsync(operationId, franchiseId, cancellationToken);
            if (receipt is not null) return ToResult(receipt);
            throw new JitenFranchiseTopologyReviewRequiredException(
                "Another topology update won the race. Review the graph again before applying changes.");
        }
    }

    private async Task<JitenFranchiseTopologyReceipt> ApplyReviewedChoicesAsync(Guid operationId,
        JitenFranchiseTopologyReview review, TrackedLocalGraph local, CancellationToken cancellationToken)
    {
        var franchise = local.Franchise ?? throw new JitenFranchiseTopologyReviewRequiredException(
            "The selected franchise no longer exists.");
        var proposalById = review.Proposals.ToDictionary(item => item.ProposalId, StringComparer.Ordinal);
        var nodeByDeck = review.Nodes.ToDictionary(item => item.DeckId);
        var stateByDeck = local.States.ToDictionary(item => item.DeckId);
        var seriesById = local.Series.ToDictionary(item => item.Id);
        var claimedSeries = new HashSet<Guid>();
        var receipt = new JitenFranchiseTopologyReceipt
        {
            Id = operationId,
            FranchiseId = franchise.Id,
            AnchorDeckId = review.AnchorDeckId,
            ReviewFingerprint = review.Fingerprint,
            CompletedAtUtc = _timeProvider.GetUtcNow()
        };

        foreach (var choice in review.ApprovedChoices!)
        {
            var proposal = proposalById[choice.ProposalId];
            var node = nodeByDeck[proposal.Node.DeckId];
            switch (choice.Action)
            {
                case JitenFranchiseNodeAction.CreateSeparateBookSeries:
                {
                    EnsureCompleteMembershipChange(review, node);
                    var created = new MediaSeries(node.Title, MediaType.Book)
                    {
                        Franchise = franchise,
                        FranchiseId = franchise.Id,
                        JitenDeckId = node.DeckId
                    };
                    _context.MediaSeries.Add(created);
                    seriesById[created.Id] = created;
                    claimedSeries.Add(created.Id);
                    UpsertState(stateByDeck, franchise, node, JitenFranchiseNodeResolution.LinkedSeries, created.Id);
                    receipt.CreatedSeries++;
                    receipt.LinkedSeries++;
                    break;
                }
                case JitenFranchiseNodeAction.LinkExistingSeries:
                {
                    EnsureCompleteMembershipChange(review, node);
                    var seriesId = choice.MediaSeriesId ?? proposal.ExactSeriesId
                        ?? throw new JitenFranchiseTopologyReviewRequiredException("Choose a series for each graph link.");
                    if (!seriesById.TryGetValue(seriesId, out var series))
                        throw new JitenFranchiseTopologyReviewRequiredException("The selected series changed. Review the graph again.");
                    if (series.MediaType != MediaType.Book || node.Classification != JitenFranchiseNodeClassification.Book)
                        throw new JitenFranchiseTopologyReviewRequiredException(
                            "Only supported book nodes can be linked to a book series in this batch.");
                    if (series.JitenDeckId is not null && series.JitenDeckId != node.DeckId)
                        throw new JitenFranchiseTopologyReviewRequiredException(
                            "The selected series already has a different Jiten identity. Do not collapse graph nodes into one series.");
                    if (!claimedSeries.Add(seriesId))
                        throw new JitenFranchiseTopologyReviewRequiredException(
                            "One series cannot represent multiple Jiten graph nodes in a single topology review.");
                    series.Franchise = franchise;
                    series.FranchiseId = franchise.Id;
                    series.JitenDeckId ??= node.DeckId;
                    UpsertState(stateByDeck, franchise, node, JitenFranchiseNodeResolution.LinkedSeries, series.Id);
                    receipt.LinkedSeries++;
                    break;
                }
                case JitenFranchiseNodeAction.Ignore:
                    UpsertState(stateByDeck, franchise, node, JitenFranchiseNodeResolution.Ignored, null);
                    receipt.IgnoredNodes++;
                    break;
                case JitenFranchiseNodeAction.KeepUnresolved:
                    UpsertState(stateByDeck, franchise, node, JitenFranchiseNodeResolution.Unresolved, null);
                    receipt.UnresolvedNodes++;
                    break;
                default:
                    throw new JitenFranchiseTopologyReviewRequiredException("The topology review contains an unsupported choice.");
            }
        }

        // An anchor is discovery provenance only; graph edges never establish
        // ordering or rewrite any series not explicitly chosen above.
        franchise.SetJitenAnchorDeckId(review.AnchorDeckId);
        _context.JitenFranchiseTopologyReceipts.Add(receipt);
        await Task.CompletedTask;
        return receipt;
    }

    private void UpsertState(Dictionary<int, JitenFranchiseGraphNodeState> stateByDeck, Franchise franchise,
        JitenFranchiseProviderNode node, JitenFranchiseNodeResolution resolution, Guid? seriesId)
    {
        if (!stateByDeck.TryGetValue(node.DeckId, out var state))
        {
            state = new JitenFranchiseGraphNodeState
            {
                Franchise = franchise,
                FranchiseId = franchise.Id,
                DeckId = node.DeckId
            };
            _context.JitenFranchiseGraphNodeStates.Add(state);
            stateByDeck.Add(node.DeckId, state);
        }

        state.Resolution = resolution;
        state.MediaSeriesId = seriesId;
        state.LastProviderTitle = node.Title;
        state.ProviderMediaType = node.ProviderMediaType;
        state.ProviderFingerprint = node.Fingerprint;
        state.UpdatedAtUtc = _timeProvider.GetUtcNow();
        state.Version = Guid.NewGuid();
    }

    private static void EnsureCompleteMembershipChange(JitenFranchiseTopologyReview review,
        JitenFranchiseProviderNode node)
    {
        if (!review.IsCompleteGraph || review.IsTruncated)
            throw new JitenFranchiseTopologyReviewRequiredException(
                $"Jiten returned an incomplete graph. Node {node.DeckId} may be ignored or kept unresolved, but series membership cannot change until a complete review is available.");
    }

    private static void ValidateChoices(JitenFranchiseTopologyReview review,
        IReadOnlyList<JitenFranchiseTopologyChoice> choices)
    {
        if (choices.Count != review.Proposals.Count || choices.Select(item => item.ProposalId).Distinct(StringComparer.Ordinal).Count() != choices.Count)
            throw new JitenFranchiseTopologyReviewRequiredException(
                "Choose an action for every graph node before confirming the topology.");
        var proposals = review.Proposals.ToDictionary(item => item.ProposalId, StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            if (!proposals.TryGetValue(choice.ProposalId, out var proposal) || !proposal.AllowedActions.Contains(choice.Action))
                throw new JitenFranchiseTopologyReviewRequiredException("The submitted graph choices no longer match the reviewed evidence.");
            if (choice.Action == JitenFranchiseNodeAction.LinkExistingSeries &&
                choice.MediaSeriesId is null && proposal.ExactSeriesId is null)
                throw new JitenFranchiseTopologyReviewRequiredException("Choose the existing series to link for this graph node.");
            if (choice.Action != JitenFranchiseNodeAction.LinkExistingSeries && choice.MediaSeriesId is not null)
                throw new JitenFranchiseTopologyReviewRequiredException("Only a link action may include a series selection.");
        }
    }

    private async Task<LocalGraph> LoadLocalAsync(Guid franchiseId, CancellationToken cancellationToken)
    {
        var franchise = await _context.Franchises.AsNoTracking().SingleOrDefaultAsync(item => item.Id == franchiseId, cancellationToken)
            ?? throw new JitenFranchiseTopologyReviewRequiredException("The selected franchise no longer exists.");
        var series = await _context.MediaSeries.AsNoTracking()
            .OrderBy(item => item.Title).ThenBy(item => item.Id)
            .Select(item => new LocalSeries(item.Id, item.Title, item.MediaType, item.JitenDeckId, item.FranchiseId))
            .ToListAsync(cancellationToken);
        var states = await _context.JitenFranchiseGraphNodeStates.AsNoTracking()
            .Where(item => item.FranchiseId == franchiseId)
            .OrderBy(item => item.DeckId)
            .Select(item => new LocalState(item.DeckId, item.Resolution, item.MediaSeriesId, item.LastProviderTitle,
                item.ProviderMediaType, item.ProviderFingerprint, item.Version))
            .ToListAsync(cancellationToken);
        return new LocalGraph(new LocalFranchise(franchise.Id, franchise.Title, franchise.JitenAnchorDeckId), series, states);
    }

    private async Task<TrackedLocalGraph> LoadTrackedAsync(Guid franchiseId, CancellationToken cancellationToken)
    {
        var franchise = await _context.Franchises.SingleOrDefaultAsync(item => item.Id == franchiseId, cancellationToken);
        if (franchise is null) return new TrackedLocalGraph(null, [], []);
        var series = await _context.MediaSeries.OrderBy(item => item.Title).ThenBy(item => item.Id).ToListAsync(cancellationToken);
        var states = await _context.JitenFranchiseGraphNodeStates.Where(item => item.FranchiseId == franchiseId)
            .OrderBy(item => item.DeckId).ToListAsync(cancellationToken);
        return new TrackedLocalGraph(franchise, series, states);
    }

    private static NormalizedGraph NormalizeGraph(JitenFranchiseDTO graph)
    {
        var duplicate = graph.Nodes.GroupBy(item => item.DeckId).Any(group => group.Key <= 0 || group.Count() != 1);
        var nodes = graph.Nodes.Where(item => item.DeckId > 0)
            .GroupBy(item => item.DeckId)
            .OrderBy(group => group.Key)
            .Select(group => group.First())
            .Take(MaxNodes)
            .Select(item =>
            {
                var title = RequiredTitle(item.OriginalTitle, item.EnglishTitle, item.DeckId);
                var classification = item.MediaType == 4
                    ? JitenFranchiseNodeClassification.Book
                    : JitenFranchiseNodeClassification.Unknown;
                return new JitenFranchiseProviderNode(item.DeckId, title, item.MediaType, classification,
                    Math.Max(0, item.CharacterCount), Math.Max(0, item.ChildrenDeckCount),
                    Hash(new { item.DeckId, title, item.MediaType, item.CharacterCount, item.ChildrenDeckCount }));
            }).ToArray();
        var ids = nodes.Select(item => item.DeckId).ToHashSet();
        var invalidEdges = graph.Edges.Any(edge => edge.SourceDeckId <= 0 || edge.TargetDeckId <= 0 ||
                                                   !ids.Contains(edge.SourceDeckId) || !ids.Contains(edge.TargetDeckId));
        var edges = graph.Edges.Where(edge => edge.SourceDeckId > 0 && edge.TargetDeckId > 0 &&
                                              ids.Contains(edge.SourceDeckId) && ids.Contains(edge.TargetDeckId))
            .OrderBy(edge => edge.SourceDeckId).ThenBy(edge => edge.TargetDeckId).ThenBy(edge => edge.RelationshipType)
            .Select(edge => new JitenFranchiseProviderEdge(edge.SourceDeckId, edge.TargetDeckId, edge.RelationshipType))
            .ToArray();
        var capped = graph.Nodes.Count > MaxNodes;
        var complete = !graph.Truncated && !duplicate && !invalidEdges && !capped;
        var warning = complete ? null : graph.Truncated
            ? "Jiten truncated the graph. Membership changes are disabled until a complete graph can be reviewed."
            : capped
                ? "The graph exceeded the bounded node limit. Membership changes are disabled until a complete graph can be reviewed."
                : "Jiten returned duplicate or incomplete graph evidence. Membership changes are disabled until it can be reviewed completely.";
        return new NormalizedGraph(nodes, edges, complete, warning);
    }

    private static string RequiredTitle(string? original, string? english, int deckId) =>
        !string.IsNullOrWhiteSpace(original) ? original.Trim() :
        !string.IsNullOrWhiteSpace(english) ? english.Trim() : $"Jiten deck {deckId}";

    private static string FingerprintProvider(int anchorDeckId, JitenFranchiseDTO graph, NormalizedGraph normalized) =>
        Hash(new { anchorDeckId, graph.Truncated, normalized.IsCompleteGraph, normalized.Nodes, normalized.Edges });

    private static string FingerprintLocal(LocalGraph graph) => Hash(new
    {
        graph.Franchise,
        Series = graph.Series.OrderBy(item => item.Id),
        States = graph.States.OrderBy(item => item.DeckId)
    });

    private static string FingerprintLocal(TrackedLocalGraph graph) => Hash(new
    {
        Franchise = graph.Franchise is null ? null : new LocalFranchise(graph.Franchise.Id, graph.Franchise.Title, graph.Franchise.JitenAnchorDeckId),
        Series = graph.Series.Select(item => new LocalSeries(item.Id, item.Title, item.MediaType, item.JitenDeckId, item.FranchiseId)).OrderBy(item => item.Id),
        States = graph.States.Select(item => new LocalState(item.DeckId, item.Resolution, item.MediaSeriesId,
            item.LastProviderTitle, item.ProviderMediaType, item.ProviderFingerprint, item.Version)).OrderBy(item => item.DeckId)
    });

    private static object ProposalShape(JitenFranchiseNodeProposal proposal) => new
    {
        proposal.ProposalId,
        proposal.Node.DeckId,
        proposal.Kind,
        proposal.ExactSeriesId,
        CandidateSeriesIds = proposal.CandidateSeriesIds.Order(),
        AllowedActions = proposal.AllowedActions.Order(),
        proposal.RecommendedAction,
        proposal.PersistedResolution
    };

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static JitenFranchiseTopologyReceiptResult ToResult(JitenFranchiseTopologyReceipt receipt) =>
        new(receipt.Id, receipt.FranchiseId, receipt.AnchorDeckId, receipt.CreatedSeries, receipt.LinkedSeries,
            receipt.IgnoredNodes, receipt.UnresolvedNodes, receipt.CompletedAtUtc);

    private sealed record NormalizedGraph(IReadOnlyList<JitenFranchiseProviderNode> Nodes,
        IReadOnlyList<JitenFranchiseProviderEdge> Edges, bool IsCompleteGraph, string? Warning);
    public sealed record LocalFranchise(Guid Id, string Title, int? JitenAnchorDeckId);
    public sealed record LocalSeries(Guid Id, string Title, MediaType MediaType, int? JitenDeckId, Guid? FranchiseId);
    public sealed record LocalState(int DeckId, JitenFranchiseNodeResolution Resolution, Guid? MediaSeriesId,
        string LastProviderTitle, int ProviderMediaType, string ProviderFingerprint, Guid Version);
    private sealed record LocalGraph(LocalFranchise Franchise, IReadOnlyList<LocalSeries> Series,
        IReadOnlyList<LocalState> States);
    private sealed record TrackedLocalGraph(Franchise? Franchise, IReadOnlyList<MediaSeries> Series,
        IReadOnlyList<JitenFranchiseGraphNodeState> States);
}

public static class JitenFranchiseTopologyPlanner
{
    public static IReadOnlyList<JitenFranchiseNodeProposal> Plan(IReadOnlyList<JitenFranchiseProviderNode> nodes,
        IReadOnlyList<JitenFranchiseTopologyService.LocalSeries> series,
        IReadOnlyList<JitenFranchiseTopologyService.LocalState> states, bool isCompleteGraph)
    {
        var stateByDeck = states.ToDictionary(item => item.DeckId);
        var duplicateNodeIds = nodes.GroupBy(item => item.DeckId).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        var candidatesByTitle = series.Where(item => item.MediaType == MediaType.Book)
            .GroupBy(item => NormalizeTitle(item.Title), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).Order().ToArray(), StringComparer.Ordinal);
        var exactByDeck = series.Where(item => item.JitenDeckId is not null)
            .GroupBy(item => item.JitenDeckId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).Order().ToArray());
        var result = new List<JitenFranchiseNodeProposal>(nodes.Count);
        foreach (var node in nodes.OrderBy(item => item.DeckId))
        {
            stateByDeck.TryGetValue(node.DeckId, out var state);
            exactByDeck.TryGetValue(node.DeckId, out var exact);
            candidatesByTitle.TryGetValue(NormalizeTitle(node.Title), out var candidates);
            candidates ??= [];
            if (duplicateNodeIds.Contains(node.DeckId))
            {
                result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.DuplicateProviderNode,
                    null, [], [JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved],
                    JitenFranchiseNodeAction.KeepUnresolved, state?.Resolution,
                    "Jiten returned this node more than once; it cannot change membership."));
                continue;
            }
            if (state?.Resolution == JitenFranchiseNodeResolution.Ignored)
            {
                result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.IgnoredDecision,
                    state.MediaSeriesId, candidates, [JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved],
                    JitenFranchiseNodeAction.Ignore, state.Resolution,
                    "This node was previously ignored and remains a durable manual decision."));
                continue;
            }
            if (node.Classification != JitenFranchiseNodeClassification.Book)
            {
                result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.UnsupportedMedia,
                    null, candidates, [JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved],
                    JitenFranchiseNodeAction.KeepUnresolved, state?.Resolution,
                    "This Jiten media type is unsupported in Batch 5A and remains explicitly unresolved."));
                continue;
            }
            if (exact is { Length: 1 })
            {
                var actions = isCompleteGraph
                    ? new[] { JitenFranchiseNodeAction.LinkExistingSeries, JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved }
                    : new[] { JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved };
                result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.ExactSeriesIdentity,
                    exact[0], candidates, actions,
                    isCompleteGraph ? JitenFranchiseNodeAction.LinkExistingSeries : JitenFranchiseNodeAction.KeepUnresolved,
                    state?.Resolution,
                    isCompleteGraph
                        ? "This node has one exact stored Jiten series identity; confirming it moves only that series."
                        : "The graph is incomplete, so this exact identity cannot change membership yet."));
                continue;
            }
            if (candidates.Length > 0 || exact is { Length: > 1 })
            {
                var actions = isCompleteGraph
                    ? new[] { JitenFranchiseNodeAction.LinkExistingSeries, JitenFranchiseNodeAction.CreateSeparateBookSeries, JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved }
                    : new[] { JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved };
                result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.CandidateSeries,
                    null, candidates, actions, JitenFranchiseNodeAction.KeepUnresolved, state?.Resolution,
                    "Title resemblance or duplicate stored identities is not proof of equivalence. Choose explicitly or keep this node unresolved."));
                continue;
            }
            var newActions = isCompleteGraph
                ? new[] { JitenFranchiseNodeAction.CreateSeparateBookSeries, JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved }
                : new[] { JitenFranchiseNodeAction.Ignore, JitenFranchiseNodeAction.KeepUnresolved };
            result.Add(new($"node:{node.DeckId}", node, JitenFranchiseNodeProposalKind.NewBookSeries,
                null, [], newActions, JitenFranchiseNodeAction.KeepUnresolved, state?.Resolution,
                isCompleteGraph
                    ? "This is separate Jiten graph evidence. Create a separate book series only after explicit approval."
                    : "The graph is incomplete, so this node remains unresolved until a complete review is available."));
        }
        return result;
    }

    private static string NormalizeTitle(string? title) => string.Concat((title ?? string.Empty)
        .Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
}

public sealed class JitenFranchiseTopologyReviewRequiredException(string message, bool canRetryCurrentReview = false)
    : InvalidOperationException(message)
{
    public bool CanRetryCurrentReview { get; } = canRetryCurrentReview;
}
