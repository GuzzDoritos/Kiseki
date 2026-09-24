using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kiseki.Core.Services;

public enum JitenCatalogueProposalKind
{
    ExactIdentity,
    Addition,
    AmbiguousAssociation,
    DuplicateProviderIdentity,
    MissingProviderEntry,
    AbsenceUnverified
}

public enum JitenCatalogueChoiceAction
{
    Apply,
    AddNew,
    LinkExisting,
    MarkMissing,
    Ignore
}

public sealed record JitenCatalogueProviderItem(
    string NormalizedKey,
    int ProviderItemId,
    int? ParentProviderItemId,
    string Title,
    int? CharacterCount,
    string? CoverUrl,
    CanonicalCoverSource CoverSource,
    ReleaseState ReleaseState,
    DateOnly? ReleaseDate,
    int ProviderOrder,
    string Fingerprint,
    string PayloadJson);

public sealed record JitenCatalogueLocalInstallment(
    Guid Id,
    Guid Version,
    string DisplayTitle,
    string? CanonicalTitle,
    string? TitleOverride,
    int? CanonicalCharacterCount,
    int? CharacterCountOverride,
    string? CanonicalCoverUrl,
    CanonicalCoverSource CanonicalCoverSource,
    ReleaseState ReleaseState,
    ReleaseState? ReleaseStateOverride,
    DateOnly? ReleaseDate,
    DateOnly? ReleaseDateOverride,
    int OrderKey,
    IReadOnlyList<string> JitenKeys,
    IReadOnlyList<string> LegacyJitenKeys);

public sealed record JitenCatalogueProposal(
    string ProposalId,
    string ProviderKey,
    JitenCatalogueProposalKind Kind,
    JitenCatalogueProviderItem? ProviderItem,
    Guid? ExistingInstallmentId,
    IReadOnlyList<Guid> CandidateInstallmentIds,
    IReadOnlyList<JitenCatalogueChoiceAction> AllowedActions,
    JitenCatalogueChoiceAction RecommendedAction,
    bool HasManualConflict,
    bool HasUncertainOrder,
    string Message);

public sealed record JitenCatalogueChoice(
    string ProposalId,
    JitenCatalogueChoiceAction Action,
    Guid? InstallmentId = null);

public sealed record JitenCatalogueReview(
    Guid ReviewId,
    Guid MediaSeriesId,
    int JitenDeckId,
    string Fingerprint,
    string ProviderFingerprint,
    string LocalFingerprint,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsCompleteFetch,
    int ExpectedItems,
    int RetrievedItems,
    string? Warning,
    IReadOnlyList<JitenCatalogueProviderItem> ProviderItems,
    IReadOnlyList<JitenCatalogueProposal> Proposals,
    IReadOnlyList<JitenCatalogueChoice>? ApprovedChoices = null,
    string? ApprovalFingerprint = null);

public sealed record JitenCatalogueRefreshReceiptResult(
    Guid OperationId,
    Guid MediaSeriesId,
    int JitenDeckId,
    int AddedInstallments,
    int LinkedIdentities,
    int UpdatedInstallments,
    int MarkedMissing,
    int Ignored,
    DateTimeOffset CompletedAtUtc);

public interface IJitenCatalogueReviewStore
{
    void Save(JitenCatalogueReview review);
    JitenCatalogueReview? Get(Guid reviewId, DateTimeOffset now);
    bool TryApprove(Guid reviewId, string expectedFingerprint,
        IReadOnlyList<JitenCatalogueChoice> choices, string approvalFingerprint, DateTimeOffset now,
        out JitenCatalogueReview? approvedReview);
}

public sealed class InMemoryJitenCatalogueReviewStore : IJitenCatalogueReviewStore
{
    private const int MaxReviews = 256;
    private readonly ConcurrentDictionary<Guid, JitenCatalogueReview> _reviews = new();

    public void Save(JitenCatalogueReview review)
    {
        foreach (var expired in _reviews.Where(item => item.Value.ExpiresAtUtc <= review.CreatedAtUtc))
            _reviews.TryRemove(expired.Key, out _);
        _reviews[review.ReviewId] = review;
        foreach (var oldest in _reviews.OrderBy(item => item.Value.CreatedAtUtc)
                     .Take(Math.Max(0, _reviews.Count - MaxReviews)))
            _reviews.TryRemove(oldest.Key, out _);
    }

    public JitenCatalogueReview? Get(Guid reviewId, DateTimeOffset now)
    {
        if (!_reviews.TryGetValue(reviewId, out var review)) return null;
        if (review.ExpiresAtUtc > now) return review;
        _reviews.TryRemove(reviewId, out _);
        return null;
    }

    public bool TryApprove(Guid reviewId, string expectedFingerprint,
        IReadOnlyList<JitenCatalogueChoice> choices, string approvalFingerprint, DateTimeOffset now,
        out JitenCatalogueReview? approvedReview)
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

public static class JitenCatalogueReconciliationPlanner
{
    public static IReadOnlyList<JitenCatalogueProposal> Plan(
        IReadOnlyList<JitenCatalogueLocalInstallment> local,
        IReadOnlyList<JitenCatalogueProviderItem> provider,
        bool isCompleteFetch,
        int jitenDeckId)
    {
        var result = new List<JitenCatalogueProposal>();
        var localByKey = local.SelectMany(item => item.JitenKeys.Select(key => (key, item)))
            .GroupBy(x => x.key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(x => x.item).ToArray(), StringComparer.Ordinal);
        var duplicateProviderKeys = provider.GroupBy(item => item.NormalizedKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var ambiguousTitles = provider.GroupBy(item => NormalizeTitle(item.Title), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var group in provider.GroupBy(item => item.NormalizedKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Min(item => item.ProviderOrder)))
        {
            var item = group.First();
            var id = $"provider:{item.NormalizedKey}";
            if (duplicateProviderKeys.Contains(item.NormalizedKey))
            {
                result.Add(new(id, item.NormalizedKey, JitenCatalogueProposalKind.DuplicateProviderIdentity,
                    item, null, [], [JitenCatalogueChoiceAction.Ignore], JitenCatalogueChoiceAction.Ignore,
                    false, true, "Jiten returned the same canonical identity more than once; it cannot be assigned automatically."));
                continue;
            }

            if (localByKey.TryGetValue(item.NormalizedKey, out var exact) && exact.Length == 1)
            {
                var installment = exact[0];
                var changed = installment.CanonicalTitle != item.Title ||
                              installment.CanonicalCharacterCount != item.CharacterCount ||
                              installment.CanonicalCoverUrl != item.CoverUrl ||
                              installment.CanonicalCoverSource != item.CoverSource ||
                              installment.ReleaseState != item.ReleaseState ||
                              installment.ReleaseDate != item.ReleaseDate;
                var manualConflict = installment.TitleOverride is not null || installment.CharacterCountOverride is not null ||
                                     installment.ReleaseStateOverride is not null ||
                                     installment.ReleaseDateOverride is not null;
                result.Add(new(id, item.NormalizedKey, JitenCatalogueProposalKind.ExactIdentity,
                    item, installment.Id, [],
                    [JitenCatalogueChoiceAction.Apply, JitenCatalogueChoiceAction.Ignore],
                    JitenCatalogueChoiceAction.Apply, manualConflict,
                    installment.OrderKey != (item.ProviderOrder + 1) * 100,
                    changed ? "Provider-owned metadata changed; manual corrections will remain effective." :
                        "The exact provider identity is unchanged."));
                continue;
            }

            var title = NormalizeTitle(item.Title);
            var candidates = local.Where(candidate => candidate.JitenKeys.Count == 0 &&
                                                      (candidate.LegacyJitenKeys.Contains(item.NormalizedKey) ||
                                                       NormalizeTitle(candidate.DisplayTitle) == title))
                .Select(candidate => candidate.Id).Order().ToArray();
            var ambiguous = candidates.Length > 0 || ambiguousTitles.Contains(title);
            var actions = candidates.Length > 0
                ? new[] { JitenCatalogueChoiceAction.LinkExisting, JitenCatalogueChoiceAction.AddNew, JitenCatalogueChoiceAction.Ignore }
                : new[] { JitenCatalogueChoiceAction.AddNew, JitenCatalogueChoiceAction.Ignore };
            result.Add(new(id, item.NormalizedKey,
                ambiguous ? JitenCatalogueProposalKind.AmbiguousAssociation : JitenCatalogueProposalKind.Addition,
                item, null, candidates, actions, JitenCatalogueChoiceAction.AddNew, false,
                ambiguousTitles.Contains(title),
                ambiguous ? "A title or edition resembles existing catalogue data; choose the identity association explicitly." :
                    "Jiten contains a catalogue installment that is not stored locally."));
        }

        var providerKeys = provider.Select(item => item.NormalizedKey).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in local.SelectMany(item => item.JitenKeys.Select(key => (key, item)))
                     .Where(x => !providerKeys.Contains(x.key) && IsChildOfReviewedDeck(x.key, jitenDeckId))
                     .OrderBy(x => x.item.OrderKey).ThenBy(x => x.item.Id))
        {
            var kind = isCompleteFetch
                ? JitenCatalogueProposalKind.MissingProviderEntry
                : JitenCatalogueProposalKind.AbsenceUnverified;
            result.Add(new($"missing:{missing.key}", missing.key, kind, null, missing.item.Id, [],
                isCompleteFetch
                    ? [JitenCatalogueChoiceAction.MarkMissing, JitenCatalogueChoiceAction.Ignore]
                    : [JitenCatalogueChoiceAction.Ignore],
                isCompleteFetch ? JitenCatalogueChoiceAction.MarkMissing : JitenCatalogueChoiceAction.Ignore,
                false, false,
                isCompleteFetch
                    ? "A complete Jiten fetch no longer contains this identity; it may be marked missing without deleting local data."
                    : "The fetch was incomplete, so absence cannot be established."));
        }

        return result;
    }

    private static bool IsChildOfReviewedDeck(string key, int jitenDeckId) =>
        key == $"deck:{jitenDeckId}" || key.StartsWith($"subdeck:{jitenDeckId}:", StringComparison.Ordinal);

    internal static string NormalizeTitle(string? title) => string.Concat((title ?? string.Empty)
        .Trim().ToUpperInvariant().Where(char.IsLetterOrDigit));
}

public sealed class JitenCatalogueReconciliationService
{
    private const int MaxInstallments = 500;
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(20);
    private readonly ImmersionDbContext _context;
    private readonly IJitenApiClient _client;
    private readonly IJitenCatalogueReviewStore _store;
    private readonly TimeProvider _timeProvider;

    public JitenCatalogueReconciliationService(ImmersionDbContext context, IJitenApiClient client,
        IJitenCatalogueReviewStore store, TimeProvider? timeProvider = null)
    {
        _context = context;
        _client = client;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<JitenCatalogueReview> PreviewAsync(Guid seriesId, int jitenDeckId,
        CancellationToken cancellationToken = default)
    {
        if (seriesId == Guid.Empty || jitenDeckId <= 0)
            throw new JitenCatalogueReviewRequiredException("Choose a valid series and Jiten deck.");

        // All network and cover normalization occurs before any database transaction.
        var fetch = await _client.GetDeckCatalogueAsync(jitenDeckId, cancellationToken);
        var providerItems = NormalizeFetch(fetch, jitenDeckId, DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime));
        var local = await LoadLocalAsync(seriesId, jitenDeckId, cancellationToken);
        var providerFingerprint = FingerprintProvider(fetch, providerItems);
        var localFingerprint = FingerprintLocal(local.Series, local.Installments);
        var proposals = JitenCatalogueReconciliationPlanner.Plan(local.Installments, providerItems, fetch.IsComplete, jitenDeckId);
        var now = _timeProvider.GetUtcNow();
        var fingerprint = Hash(new
        {
            seriesId,
            jitenDeckId,
            providerFingerprint,
            localFingerprint,
            proposals = proposals.Select(ProposalFingerprintShape)
        });
        var review = new JitenCatalogueReview(Guid.NewGuid(), seriesId, jitenDeckId, fingerprint,
            providerFingerprint, localFingerprint, now, now + ReviewLifetime, fetch.IsComplete,
            fetch.ExpectedItems, fetch.RetrievedItems, fetch.Warning, providerItems, proposals);
        _store.Save(review);
        return review;
    }

    public JitenCatalogueReview Approve(Guid reviewId, string expectedFingerprint,
        IReadOnlyList<JitenCatalogueChoice> choices)
    {
        var now = _timeProvider.GetUtcNow();
        var review = _store.Get(reviewId, now)
            ?? throw new JitenCatalogueReviewRequiredException("The catalogue review expired. Fetch and review Jiten again.");
        if (review.Fingerprint != expectedFingerprint)
            throw new JitenCatalogueReviewRequiredException("The reviewed evidence changed. Review the refreshed catalogue before confirming.");
        ValidateChoices(review, choices);
        var normalized = choices.OrderBy(choice => choice.ProposalId, StringComparer.Ordinal).ToArray();
        var approvalFingerprint = Hash(new { review.Fingerprint, Choices = normalized });
        if (!_store.TryApprove(reviewId, expectedFingerprint, normalized, approvalFingerprint, now, out var approved) || approved is null)
            throw new JitenCatalogueReviewRequiredException("The catalogue review changed or expired. Review it again.");
        return approved;
    }

    public JitenCatalogueReview? GetActiveReview(Guid reviewId) =>
        reviewId == Guid.Empty ? null : _store.Get(reviewId, _timeProvider.GetUtcNow());

    public Task<JitenCatalogueRefreshReceipt?> GetReceiptAsync(Guid operationId, Guid mediaSeriesId,
        CancellationToken cancellationToken = default) =>
        _context.JitenCatalogueRefreshReceipts.AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.Id == operationId && receipt.MediaSeriesId == mediaSeriesId,
                cancellationToken);

    public async Task<JitenCatalogueRefreshReceiptResult> ApplyAsync(Guid operationId, Guid mediaSeriesId,
        Guid reviewId, string approvalFingerprint, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || mediaSeriesId == Guid.Empty || reviewId == Guid.Empty ||
            string.IsNullOrWhiteSpace(approvalFingerprint))
            throw new JitenCatalogueReviewRequiredException(
                "A valid approved review and operation ID are required.", canRetryCurrentReview: true);

        var existingReceipt = await GetReceiptAsync(operationId, mediaSeriesId, cancellationToken);
        if (existingReceipt is not null) return ToResult(existingReceipt);

        var now = _timeProvider.GetUtcNow();
        var review = _store.Get(reviewId, now)
            ?? throw new JitenCatalogueReviewRequiredException("The catalogue review expired. Fetch and review Jiten again.");
        if (review.MediaSeriesId != mediaSeriesId)
            throw new JitenCatalogueReviewRequiredException(
                "The approved catalogue review does not belong to this series. Fetch and review Jiten again.");
        if (review.ApprovedChoices is null || review.ApprovalFingerprint != approvalFingerprint)
            throw new JitenCatalogueReviewRequiredException("Approve the server-held catalogue choices before applying them.");

        // Confirmation re-fetch is deliberately outside the database transaction.
        var freshFetch = await _client.GetDeckCatalogueAsync(review.JitenDeckId, cancellationToken);
        var freshItems = NormalizeFetch(freshFetch, review.JitenDeckId,
            DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime));
        if (FingerprintProvider(freshFetch, freshItems) != review.ProviderFingerprint)
            throw new JitenCatalogueReviewRequiredException("Jiten changed since the preview. Review the refreshed catalogue before confirming.");

        try
        {
            return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                var replay = await GetReceiptAsync(operationId, mediaSeriesId, cancellationToken);
                if (replay is not null) return ToResult(replay);

                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                var local = await LoadTrackedAsync(review.MediaSeriesId, cancellationToken);
                if (FingerprintLocal(local.Series, local.Installments.Select(ToLocal).ToArray()) != review.LocalFingerprint)
                    throw new JitenCatalogueReviewRequiredException(
                        "The local catalogue changed since the preview. Review it again before applying Jiten data.");

                var receipt = new JitenCatalogueRefreshReceipt
                {
                    Id = operationId,
                    MediaSeriesId = review.MediaSeriesId,
                    JitenDeckId = review.JitenDeckId,
                    ReviewFingerprint = review.Fingerprint,
                    CompletedAtUtc = _timeProvider.GetUtcNow()
                };
                var proposalById = review.Proposals.ToDictionary(item => item.ProposalId, StringComparer.Ordinal);
                var installmentById = local.Installments.ToDictionary(item => item.Id);
                var nextOrder = local.Installments.Count == 0 ? 100 : local.Installments.Max(item => item.OrderKey) + 100;
                var reviewedKeys = review.ProviderItems.Select(item => item.NormalizedKey).Distinct().ToArray();
                var reviewedFingerprints = review.ProviderItems.Select(item => item.Fingerprint).Distinct().ToArray();
                var existingSnapshotRows = await _context.InstallmentProviderSnapshots
                    .Where(snapshot => snapshot.Provider == "jiten" &&
                                       reviewedKeys.Contains(snapshot.NormalizedKey) &&
                                       reviewedFingerprints.Contains(snapshot.Fingerprint))
                    .Select(snapshot => new { snapshot.NormalizedKey, snapshot.Fingerprint })
                    .ToListAsync(cancellationToken);
                var existingSnapshots = existingSnapshotRows
                    .Select(snapshot => SnapshotPair(snapshot.NormalizedKey, snapshot.Fingerprint))
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var choice in review.ApprovedChoices)
                {
                    var proposal = proposalById[choice.ProposalId];
                    if (choice.Action == JitenCatalogueChoiceAction.Ignore)
                    {
                        receipt.Ignored++;
                        continue;
                    }

                    if (choice.Action == JitenCatalogueChoiceAction.MarkMissing)
                    {
                        var missingIdentity = FindIdentity(local.Installments, proposal.ProviderKey)
                            ?? throw new JitenCatalogueReviewRequiredException("A reviewed provider identity is no longer present.");
                        missingIdentity.MissingSinceUtc ??= receipt.CompletedAtUtc;
                        local.Installments.Single(item => item.Id == missingIdentity.MediaInstallmentId).Version = Guid.NewGuid();
                        receipt.MarkedMissing++;
                        continue;
                    }

                    var providerItem = proposal.ProviderItem
                        ?? throw new JitenCatalogueReviewRequiredException("Reviewed provider metadata is missing.");
                    MediaInstallment installment;
                    InstallmentProviderIdentity identity;
                    if (choice.Action == JitenCatalogueChoiceAction.AddNew)
                    {
                        installment = new MediaInstallment(providerItem.Title, MediaType.Book, nextOrder)
                        {
                            MediaSeries = local.Series,
                            MediaSeriesId = local.Series.Id,
                            LegacyTitle = null,
                            Kind = InstallmentKind.Volume
                        };
                        nextOrder += 100;
                        _context.MediaInstallments.Add(installment);
                        local.Installments.Add(installment);
                        identity = CreateIdentity(installment, providerItem);
                        _context.InstallmentProviderIdentities.Add(identity);
                        receipt.AddedInstallments++;
                        receipt.LinkedIdentities++;
                    }
                    else if (choice.Action == JitenCatalogueChoiceAction.LinkExisting)
                    {
                        installment = installmentById[choice.InstallmentId!.Value];
                        if (installment.ProviderIdentities.Any(item => item.Provider == "jiten"))
                            throw new JitenCatalogueReviewRequiredException("The selected installment acquired a provider identity. Review again.");
                        identity = CreateIdentity(installment, providerItem);
                        _context.InstallmentProviderIdentities.Add(identity);
                        installment.Version = Guid.NewGuid();
                        receipt.LinkedIdentities++;
                    }
                    else
                    {
                        installment = installmentById[proposal.ExistingInstallmentId!.Value];
                        identity = installment.ProviderIdentities.Single(item =>
                            item.Provider == "jiten" && item.NormalizedKey == providerItem.NormalizedKey);
                    }

                    if (ApplyProviderMetadata(installment, identity, providerItem, receipt.CompletedAtUtc,
                        review.IsCompleteFetch,
                        existingSnapshots.Contains(SnapshotPair(providerItem.NormalizedKey, providerItem.Fingerprint))))
                        receipt.UpdatedInstallments++;
                    existingSnapshots.Add(SnapshotPair(providerItem.NormalizedKey, providerItem.Fingerprint));
                }

                local.Series.JitenDeckId = review.JitenDeckId;
                _context.JitenCatalogueRefreshReceipts.Add(receipt);
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToResult(receipt);
            });
        }
        catch (Exception exception) when (IsCollision(exception))
        {
            _context.ChangeTracker.Clear();
            var committed = await GetReceiptAsync(operationId, mediaSeriesId, cancellationToken);
            if (committed is not null) return ToResult(committed);
            throw new JitenCatalogueReviewRequiredException(
                "Another catalogue refresh changed these identities. Fetch and review Jiten again.");
        }
    }

    private async Task<(MediaSeries Series, IReadOnlyList<JitenCatalogueLocalInstallment> Installments)> LoadLocalAsync(
        Guid seriesId, int jitenDeckId, CancellationToken cancellationToken)
    {
        var series = await _context.MediaSeries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == seriesId, cancellationToken)
            ?? throw new JitenCatalogueReviewRequiredException("The selected series no longer exists.");
        if (series.MediaType != MediaType.Book)
            throw new JitenCatalogueReviewRequiredException("Jiten catalogue refresh currently supports book series only.");
        if (series.JitenDeckId is int currentDeckId && currentDeckId != jitenDeckId)
            throw new JitenCatalogueReviewRequiredException(
                $"This series is linked to Jiten deck {currentDeckId}. Change that association explicitly before reviewing another deck.");
        var entities = await _context.MediaInstallments.AsNoTracking()
            .Where(item => item.MediaSeriesId == seriesId).OrderBy(item => item.OrderKey).ThenBy(item => item.Id)
            .Include(item => item.ProviderIdentities)
            .Include(item => item.Copies)
            .AsSplitQuery()
            .Take(MaxInstallments + 1).ToListAsync(cancellationToken);
        if (entities.Count > MaxInstallments)
            throw new JitenCatalogueReviewRequiredException("This series is too large for one catalogue refresh.");
        var installments = entities.Select(ToLocal).ToArray();
        return (series, installments);
    }

    private async Task<(MediaSeries Series, List<MediaInstallment> Installments)> LoadTrackedAsync(
        Guid seriesId, CancellationToken cancellationToken)
    {
        var series = await _context.MediaSeries.SingleOrDefaultAsync(item => item.Id == seriesId, cancellationToken)
            ?? throw new JitenCatalogueReviewRequiredException("The selected series no longer exists.");
        var installments = await _context.MediaInstallments
            .Where(item => item.MediaSeriesId == seriesId).OrderBy(item => item.OrderKey).ThenBy(item => item.Id)
            .Include(item => item.ProviderIdentities)
            .Include(item => item.Copies)
            .Take(MaxInstallments + 1).ToListAsync(cancellationToken);
        if (series.MediaType != MediaType.Book || installments.Count > MaxInstallments)
            throw new JitenCatalogueReviewRequiredException("The series is no longer eligible for this Jiten refresh.");
        return (series, installments);
    }

    private static IReadOnlyList<JitenCatalogueProviderItem> NormalizeFetch(
        JitenDeckCatalogueFetchResult fetch, int requestedDeckId, DateOnly today)
    {
        if (fetch.Detail is null)
            throw new JitenCatalogueReviewRequiredException(fetch.Warning ?? "Jiten could not find the requested deck.");
        var parent = new[] { fetch.Detail.MainDeck, fetch.Detail.ParentDeck }
            .FirstOrDefault(deck => deck?.DeckId == requestedDeckId)
            ?? throw new JitenCatalogueReviewRequiredException("Jiten returned a different parent deck. Review cannot continue.");
        if (parent.DeckId <= 0 || parent.CharacterCount < 0 || fetch.Detail.SubDecks.Any(item => item.DeckId <= 0 || item.CharacterCount < 0))
            throw new JitenCatalogueReviewRequiredException("Jiten returned an invalid identity or character count.");

        var source = fetch.Detail.SubDecks.Count > 0 || parent.ChildrenDeckCount > 0
            ? fetch.Detail.SubDecks.Select((deck, index) => (selection: JitenMediaSelection.FromSubdeck(parent, deck), deck, index))
            : [(JitenMediaSelection.FromDeck(parent), parent, 0)];
        return source.Select(tuple =>
        {
            var selection = tuple.selection;
            var key = selection.SubdeckId is int child
                ? $"subdeck:{selection.DeckId}:{child}"
                : $"deck:{selection.DeckId}";
            DateOnly? releaseDate = tuple.deck.ReleaseDate is DateTime date ? DateOnly.FromDateTime(date) : null;
            var releaseState = releaseDate is null ? ReleaseState.Unknown :
                releaseDate > today ? ReleaseState.Upcoming : ReleaseState.Released;
            var coverSource = selection.CoverEvidence switch
            {
                JitenCoverEvidence.Specific => CanonicalCoverSource.ProviderExact,
                JitenCoverEvidence.ParentFallback => CanonicalCoverSource.ProviderParentFallback,
                _ => CanonicalCoverSource.None
            };
            var payload = JsonSerializer.Serialize(new
            {
                selection.DeckId,
                selection.SubdeckId,
                selection.OriginalTitle,
                selection.RomajiTitle,
                selection.EnglishTitle,
                selection.CharacterCount,
                selection.CoverUrl,
                ReleaseDate = releaseDate
            });
            var itemFingerprint = Hash(new { key, selection.DisplayTitle, selection.CharacterCount,
                selection.CoverUrl, coverSource, releaseState, releaseDate, tuple.index });
            return new JitenCatalogueProviderItem(key, selection.SubdeckId ?? selection.DeckId,
                selection.SubdeckId.HasValue ? selection.DeckId : null, selection.DisplayTitle,
                selection.CharacterCount > 0 ? selection.CharacterCount : null, selection.CoverUrl,
                coverSource, releaseState, releaseDate, tuple.index, itemFingerprint, payload);
        }).ToArray();
    }

    private static void ValidateChoices(JitenCatalogueReview review, IReadOnlyList<JitenCatalogueChoice> choices)
    {
        if (choices.Count != review.Proposals.Count || choices.Select(choice => choice.ProposalId).Distinct().Count() != choices.Count)
            throw new JitenCatalogueReviewRequiredException(
                "Choose one action for every catalogue proposal.", canRetryCurrentReview: true);
        var proposed = review.Proposals.ToDictionary(item => item.ProposalId, StringComparer.Ordinal);
        if (choices.Where(choice => choice.Action == JitenCatalogueChoiceAction.LinkExisting)
            .GroupBy(choice => choice.InstallmentId).Any(group => group.Count() > 1))
            throw new JitenCatalogueReviewRequiredException(
                "One installment cannot receive multiple reviewed Jiten identities in the same refresh.",
                canRetryCurrentReview: true);
        foreach (var choice in choices)
        {
            if (!proposed.TryGetValue(choice.ProposalId, out var proposal) || !proposal.AllowedActions.Contains(choice.Action))
                throw new JitenCatalogueReviewRequiredException(
                    "A catalogue choice is not valid for the reviewed proposal.", canRetryCurrentReview: true);
            if (choice.Action == JitenCatalogueChoiceAction.LinkExisting &&
                (!choice.InstallmentId.HasValue || !proposal.CandidateInstallmentIds.Contains(choice.InstallmentId.Value)))
                throw new JitenCatalogueReviewRequiredException(
                    "Choose one of the reviewed candidate installments.", canRetryCurrentReview: true);
            if (choice.Action != JitenCatalogueChoiceAction.LinkExisting && choice.InstallmentId.HasValue)
                throw new JitenCatalogueReviewRequiredException(
                    "An installment target is only valid when linking an existing entry.", canRetryCurrentReview: true);
        }
    }

    private static InstallmentProviderIdentity CreateIdentity(MediaInstallment installment,
        JitenCatalogueProviderItem item) => new()
    {
        Provider = "jiten",
        NormalizedKey = item.NormalizedKey,
        MediaInstallment = installment,
        MediaInstallmentId = installment.Id,
        ProviderItemId = item.ProviderItemId,
        ParentProviderItemId = item.ParentProviderItemId
    };

    private static bool ApplyProviderMetadata(MediaInstallment installment, InstallmentProviderIdentity identity,
        JitenCatalogueProviderItem item, DateTimeOffset observedAt, bool isCompleteFetch, bool snapshotExists)
    {
        var changed = identity.MissingSinceUtc is not null || installment.CanonicalTitle != item.Title ||
                      installment.CanonicalCharacterCount != item.CharacterCount ||
                      installment.CanonicalCoverUrl != item.CoverUrl ||
                      installment.CanonicalCoverSource != item.CoverSource ||
                      installment.ReleaseState != item.ReleaseState || installment.ReleaseDate != item.ReleaseDate;
        installment.CanonicalTitle = item.Title;
        installment.CanonicalCharacterCount = item.CharacterCount;
        installment.CanonicalCoverUrl = item.CoverUrl;
        installment.CanonicalCoverSource = item.CoverSource;
        installment.ReleaseState = item.ReleaseState;
        installment.ReleaseDate = item.ReleaseDate;
        identity.LastSeenAtUtc = observedAt;
        identity.MissingSinceUtc = null;
        if (changed) installment.Version = Guid.NewGuid();
        if (!snapshotExists)
        {
            identity.Snapshots.Add(new InstallmentProviderSnapshot
            {
                Provider = "jiten",
                NormalizedKey = item.NormalizedKey,
                Fingerprint = item.Fingerprint,
                Title = item.Title,
                CharacterCount = item.CharacterCount,
                CoverUrl = item.CoverUrl,
                CoverSource = item.CoverSource,
                ReleaseState = item.ReleaseState,
                ReleaseDate = item.ReleaseDate,
                ProviderOrder = item.ProviderOrder,
                PayloadJson = item.PayloadJson,
                ObservedAtUtc = observedAt,
                IsComplete = isCompleteFetch
            });
        }
        return changed;
    }

    private static InstallmentProviderIdentity? FindIdentity(IEnumerable<MediaInstallment> installments, string key) =>
        installments.SelectMany(item => item.ProviderIdentities)
            .SingleOrDefault(identity => identity.Provider == "jiten" && identity.NormalizedKey == key);

    private static JitenCatalogueLocalInstallment ToLocal(MediaInstallment item) => new(
        item.Id, item.Version, item.DisplayTitle, item.CanonicalTitle, item.TitleOverride,
        item.CanonicalCharacterCount, item.CharacterCountOverride, item.CanonicalCoverUrl, item.CanonicalCoverSource,
        item.ReleaseState, item.ReleaseStateOverride, item.ReleaseDate, item.ReleaseDateOverride,
        item.OrderKey, item.ProviderIdentities.Where(identity => identity.Provider == "jiten")
            .Select(identity => identity.NormalizedKey).OrderBy(key => key).ToArray(),
        item.Copies.Where(copy => copy.JitenDeckId != null)
            .Select(copy => copy.JitenSubdeckId == null
                ? "deck:" + copy.JitenDeckId
                : "subdeck:" + copy.JitenDeckId + ":" + copy.JitenSubdeckId)
            .Distinct().OrderBy(key => key).ToArray());

    private static string FingerprintProvider(JitenDeckCatalogueFetchResult fetch,
        IReadOnlyList<JitenCatalogueProviderItem> items) => Hash(new
        {
            fetch.IsComplete,
            fetch.ExpectedItems,
            fetch.RetrievedItems,
            Items = items.OrderBy(item => item.ProviderOrder).ThenBy(item => item.NormalizedKey)
                .Select(item => new { item.NormalizedKey, item.Fingerprint })
        });

    private static string FingerprintLocal(MediaSeries series,
        IReadOnlyList<JitenCatalogueLocalInstallment> installments) => Hash(new
        {
            series.Id,
            series.Title,
            series.MediaType,
            series.JitenDeckId,
            Installments = installments.OrderBy(item => item.OrderKey).ThenBy(item => item.Id).Select(item => new
            {
                item.Id, item.Version, item.DisplayTitle, item.CanonicalTitle, item.TitleOverride,
                item.CanonicalCharacterCount, item.CharacterCountOverride, item.CanonicalCoverUrl, item.CanonicalCoverSource,
                item.ReleaseState, item.ReleaseStateOverride, item.ReleaseDate, item.ReleaseDateOverride,
                item.OrderKey, Keys = item.JitenKeys.OrderBy(key => key),
                LegacyKeys = item.LegacyJitenKeys.OrderBy(key => key)
            })
        });

    private static object ProposalFingerprintShape(JitenCatalogueProposal proposal) => new
    {
        proposal.ProposalId, proposal.Kind, proposal.ExistingInstallmentId,
        Candidates = proposal.CandidateInstallmentIds.Order(), proposal.AllowedActions,
        proposal.HasManualConflict, proposal.HasUncertainOrder,
        Provider = proposal.ProviderItem?.Fingerprint
    };

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value)));

    private static string SnapshotPair(string key, string fingerprint) => $"{key}\u001f{fingerprint}";

    private static JitenCatalogueRefreshReceiptResult ToResult(JitenCatalogueRefreshReceipt receipt) => new(
        receipt.Id, receipt.MediaSeriesId, receipt.JitenDeckId, receipt.AddedInstallments,
        receipt.LinkedIdentities, receipt.UpdatedInstallments, receipt.MarkedMissing,
        receipt.Ignored, receipt.CompletedAtUtc);

    private static bool IsCollision(Exception exception) => exception is DbUpdateConcurrencyException ||
        exception is PostgresException { SqlState: "23505" or "40001" or "40P01" } ||
        exception is SqliteException { SqliteErrorCode: 5 or 6 or 19 } ||
        exception.InnerException is not null && IsCollision(exception.InnerException);
}

public sealed class JitenCatalogueReviewRequiredException(
    string message,
    bool canRetryCurrentReview = false) : InvalidOperationException(message)
{
    public bool CanRetryCurrentReview { get; } = canRetryCurrentReview;
}
