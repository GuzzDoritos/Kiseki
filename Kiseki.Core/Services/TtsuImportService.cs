using System.Data;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kiseki.Core.Services;

public sealed class TtsuImportService(ImmersionDbContext context)
{
    public async Task<IReadOnlyList<TtsuTarget>> GetTargetsAsync(CancellationToken cancellationToken = default) =>
        await context.MediaWorks.AsNoTracking().Where(x => x.MediaType == MediaType.Book)
            .OrderBy(x => x.Title).Select(x => new TtsuTarget(x.Id, x.Title)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TtsuInstallmentTarget>> GetInstallmentTargetsAsync(
        CancellationToken cancellationToken = default) =>
        await context.MediaInstallments.AsNoTracking()
            .Where(x => x.MediaType == MediaType.Book)
            .OrderBy(x => x.TitleOverride ?? x.CanonicalTitle ?? x.LegacyTitle)
            .Select(x => new TtsuInstallmentTarget(
                x.Id,
                x.TitleOverride ?? x.CanonicalTitle ?? x.LegacyTitle ?? string.Empty,
                x.Version,
                x.MediaSeriesId,
                x.Copies.Count))
            .ToListAsync(cancellationToken);

    public async Task<TtsuImportTargetReview> ReviewTargetAsync(
        TtsuBookContainer book,
        TtsuProviderIdentityHint? providerIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        var normalizedTitle = TtsuBookImporter.NormalizeTitle(book.Title);
        var bindingRows = await context.TtsuBindings.AsNoTracking().ToListAsync(cancellationToken);
        var matchingBindings = bindingRows.Where(binding => SourceMatches(binding, normalizedTitle, book.FolderHint)).ToList();

        var relevantWorkIds = matchingBindings.Select(binding => binding.MediaWorkId).ToHashSet();
        Guid? providerInstallmentId = null;
        if (providerIdentity is not null)
        {
            providerInstallmentId = await context.InstallmentProviderIdentities.AsNoTracking()
                .Where(identity => identity.Provider == providerIdentity.Provider &&
                    identity.NormalizedKey == providerIdentity.NormalizedKey)
                .Select(identity => (Guid?)identity.MediaInstallmentId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var basicWorks = await context.MediaWorks.AsNoTracking()
                .Where(work => work.MediaType == MediaType.Book)
                .Select(work => new { work.Id, work.Title, work.JitenDeckId, work.JitenSubdeckId })
                .ToListAsync(cancellationToken);
        var titleWorkIds = basicWorks
            .Where(work => TtsuBookImporter.NormalizeTitle(work.Title) == normalizedTitle)
            .Select(work => work.Id)
            .ToHashSet();
        relevantWorkIds.UnionWith(titleWorkIds);
        var legacyProviderWorkIds = providerIdentity is { Provider: "jiten" }
            ? basicWorks.Where(work => LegacyJitenKey(work.JitenDeckId, work.JitenSubdeckId) == providerIdentity.NormalizedKey)
                .Select(work => work.Id).ToHashSet()
            : [];
        relevantWorkIds.UnionWith(legacyProviderWorkIds);

        if (providerInstallmentId is Guid exactInstallmentId)
        {
            relevantWorkIds.UnionWith(await context.MediaWorks.AsNoTracking()
                .Where(work => work.MediaInstallmentId == exactInstallmentId)
                .Select(work => work.Id)
                .ToListAsync(cancellationToken));
        }

        var workRows = await context.MediaWorks.AsNoTracking()
            .Where(work => relevantWorkIds.Contains(work.Id))
            .Select(work => new
            {
                work.Id,
                work.Title,
                work.Version,
                work.MediaInstallmentId,
                InstallmentVersion = work.MediaInstallment == null ? (Guid?)null : work.MediaInstallment.Version,
                SeriesId = work.MediaInstallment == null ? work.MediaSeriesId : work.MediaInstallment.MediaSeriesId,
                LifetimeCharacters = work.Logs.Sum(log => (long?)log.CharactersRead) ?? 0
            })
            .ToListAsync(cancellationToken);
        var bindingByWork = bindingRows.ToDictionary(binding => binding.MediaWorkId);
        var copyCandidates = workRows.Select(work =>
        {
            bindingByWork.TryGetValue(work.Id, out var binding);
            return new TtsuCopyTargetCandidate(
                work.Id,
                work.Title,
                work.Version,
                work.MediaInstallmentId,
                work.InstallmentVersion,
                work.SeriesId,
                binding is not null,
                binding is not null && SourceMatches(binding, normalizedTitle, book.FolderHint),
                binding?.Version,
                work.LifetimeCharacters);
        }).OrderBy(candidate => candidate.Title).ThenBy(candidate => candidate.WorkId).ToList();

        var relevantInstallmentIds = copyCandidates.Where(candidate => candidate.InstallmentId.HasValue)
            .Select(candidate => candidate.InstallmentId!.Value).ToHashSet();
        if (providerInstallmentId.HasValue)
        {
            relevantInstallmentIds.Add(providerInstallmentId.Value);
        }

        var titleInstallmentIds = (await context.MediaInstallments.AsNoTracking()
                .Where(installment => installment.MediaType == MediaType.Book)
                .Select(installment => new
                {
                    installment.Id,
                    Title = installment.TitleOverride ?? installment.CanonicalTitle ?? installment.LegacyTitle ?? string.Empty
                })
                .ToListAsync(cancellationToken))
            .Where(installment => TtsuBookImporter.NormalizeTitle(installment.Title) == normalizedTitle)
            .Select(installment => installment.Id)
            .ToList();
        relevantInstallmentIds.UnionWith(titleInstallmentIds);

        var installmentRows = await context.MediaInstallments.AsNoTracking()
            .Where(installment => relevantInstallmentIds.Contains(installment.Id))
            .Select(installment => new
            {
                installment.Id,
                Title = installment.TitleOverride ?? installment.CanonicalTitle ?? installment.LegacyTitle ?? string.Empty,
                installment.Version,
                installment.MediaSeriesId,
                CopyIds = installment.Copies.Select(copy => copy.Id).ToList(),
                ProviderKeys = installment.ProviderIdentities
                    .Select(identity => identity.Provider + ":" + identity.NormalizedKey).ToList()
            })
            .ToListAsync(cancellationToken);
        var installmentCandidates = installmentRows.Select(installment => new TtsuInstallmentTargetCandidate(
                installment.Id,
                installment.Title,
                installment.Version,
                installment.MediaSeriesId,
                installment.CopyIds.Count,
                installment.CopyIds.Count(id => !bindingByWork.ContainsKey(id)),
                installment.ProviderKeys.Order(StringComparer.Ordinal).ToList()))
            .OrderBy(candidate => candidate.Title).ThenBy(candidate => candidate.InstallmentId).ToList();
        // Provider evidence affects copy assignment only when that identity is
        // already persisted. A newly suggested provider item remains optional
        // metadata and must not make a reading-history import depend on the
        // enrichment endpoint being available at confirmation time.
        var matchedProviderIdentity = providerInstallmentId.HasValue || legacyProviderWorkIds.Count > 0
            ? providerIdentity
            : null;

        if (matchingBindings.Count == 1)
        {
            var match = copyCandidates.SingleOrDefault(candidate => candidate.WorkId == matchingBindings[0].MediaWorkId);
            if (match is not null)
            {
                return new(TtsuImportTargetChoice.ExistingCopy(match.WorkId),
                    "Previously confirmed TTSU source; its existing copy takes precedence.", false,
                    copyCandidates, installmentCandidates, matchedProviderIdentity);
            }
        }
        if (matchingBindings.Count > 1)
        {
            return new(null, "Multiple persisted source hints match. Choose the copy explicitly.", true,
                copyCandidates, installmentCandidates, matchedProviderIdentity);
        }

        if (providerInstallmentId is Guid providerId)
        {
            var unboundCopies = copyCandidates.Where(candidate =>
                candidate.InstallmentId == providerId && !candidate.HasBinding).ToList();
            if (unboundCopies.Count == 1)
            {
                return new(TtsuImportTargetChoice.ExistingCopy(unboundCopies[0].WorkId),
                    "Exact provider identity with one unbound copy; confirm whether it is this edition.", false,
                    copyCandidates, installmentCandidates, matchedProviderIdentity);
            }

            return new(TtsuImportTargetChoice.NewCopy(providerId),
                unboundCopies.Count > 1
                    ? "Exact provider installment found, but several unbound copies could match. Choose a copy or keep a separate history."
                    : "Exact provider installment found; create a separate tracked copy for this TTSU history.",
                unboundCopies.Count > 1, copyCandidates, installmentCandidates, matchedProviderIdentity);
        }

        if (legacyProviderWorkIds.Count == 1)
        {
            var legacyCopy = copyCandidates.Single(candidate => legacyProviderWorkIds.Contains(candidate.WorkId));
            if (!legacyCopy.HasBinding)
            {
                return new(TtsuImportTargetChoice.ExistingCopy(legacyCopy.WorkId),
                    "Exact legacy provider claim with one unbound copy; confirm this edition.", false,
                    copyCandidates, installmentCandidates, matchedProviderIdentity);
            }
            if (legacyCopy.InstallmentId is Guid legacyInstallmentId)
            {
                return new(TtsuImportTargetChoice.NewCopy(legacyInstallmentId),
                    "The provider-matched copy already has another TTSU history; create a separate copy under its installment.", false,
                    copyCandidates, installmentCandidates, matchedProviderIdentity);
            }
        }
        if (legacyProviderWorkIds.Count > 1)
        {
            return new(null,
                "Multiple legacy copies claim this provider identity. Choose the canonical installment or copy explicitly.", true,
                copyCandidates, installmentCandidates, matchedProviderIdentity);
        }

        var titleCopies = copyCandidates.Where(candidate => titleWorkIds.Contains(candidate.WorkId) && !candidate.HasBinding).ToList();
        var titleInstallments = installmentCandidates.Where(candidate => titleInstallmentIds.Contains(candidate.InstallmentId)).ToList();
        if (titleCopies.Count == 1 && titleInstallments.Count <= 1)
        {
            return new(TtsuImportTargetChoice.ExistingCopy(titleCopies[0].WorkId),
                "Matching unbound copy title; confirm this edition explicitly.", false,
                copyCandidates, installmentCandidates, matchedProviderIdentity);
        }
        if (titleCopies.Count == 0 && titleInstallments.Count == 1)
        {
            return new(TtsuImportTargetChoice.NewCopy(titleInstallments[0].InstallmentId),
                "Matching catalogue installment; create a tracked copy beneath it.", false,
                copyCandidates, installmentCandidates, matchedProviderIdentity);
        }
        if (titleCopies.Count + titleInstallments.Count > 1)
        {
            return new(null, "Multiple title or copy matches require an explicit choice.", true,
                copyCandidates, installmentCandidates, matchedProviderIdentity);
        }

        return new(TtsuImportTargetChoice.NewInstallment(),
            "No persisted source, provider identity, or unique title match; create a new installment and copy.", false,
            copyCandidates, installmentCandidates, matchedProviderIdentity);
    }

    public async Task<TtsuMatch> MatchAsync(TtsuBookContainer book, CancellationToken cancellationToken = default)
    {
        var review = await ReviewTargetAsync(book, cancellationToken: cancellationToken);
        return new(
            review.SuggestedChoice?.Intent == TtsuCopyIntent.ExistingCopy
                ? review.SuggestedChoice.WorkId
                : null,
            review.Reason,
            review.IsAmbiguous);
    }

    public async Task<IReadOnlyList<ImmersionLog>> GetOrphansAsync(CancellationToken cancellationToken = default) =>
        (await context.ImmersionLogs.AsNoTracking().Where(x => x.MediaWorkId == null).ToListAsync(cancellationToken))
            .Where(IsTtsu).OrderBy(x => x.Date).ToList();

    public async Task<TtsuImportPlan> PreviewAsync(TtsuBookContainer book, Guid? targetId,
        IReadOnlyDictionary<DateOnly, string>? resolutions = null, IReadOnlyList<Guid>? orphanLogIds = null,
        CancellationToken cancellationToken = default, string? progressResolution = null)
        => await PreviewTargetAsync(
            book,
            targetId is Guid id
                ? TtsuImportTargetChoice.ExistingCopy(id)
                : TtsuImportTargetChoice.NewInstallment(),
            resolutions,
            orphanLogIds,
            cancellationToken,
            progressResolution);

    public async Task<TtsuImportPlan> PreviewTargetAsync(
        TtsuBookContainer book,
        TtsuImportTargetChoice targetChoice,
        IReadOnlyDictionary<DateOnly, string>? resolutions = null,
        IReadOnlyList<Guid>? orphanLogIds = null,
        CancellationToken cancellationToken = default,
        string? progressResolution = null,
        TtsuProviderIdentityHint? providerIdentity = null)
    {
        var choiceError = ValidateTargetChoice(targetChoice);
        var targetId = targetChoice.Intent == TtsuCopyIntent.ExistingCopy ? targetChoice.WorkId : null;
        var work = targetId is null ? null : await context.MediaWorks.AsNoTracking()
            .Include(x => x.Logs).Include(x => x.MediaSeries)
            .Include(x => x.MediaInstallment).ThenInclude(x => x!.MediaSeries)
            .SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken);
        var binding = targetId is null ? null : await context.TtsuBindings.AsNoTracking().SingleOrDefaultAsync(x => x.MediaWorkId == targetId, cancellationToken);
        var targetReview = await ReviewTargetAsync(book, providerIdentity, cancellationToken);
        var selectedInstallment = targetChoice.Intent == TtsuCopyIntent.NewCopyUnderExistingInstallment &&
            targetChoice.InstallmentId is Guid selectedInstallmentId
            ? await GetInstallmentStateAsync(selectedInstallmentId, cancellationToken)
            : null;
        var targetStateError = await ValidateTargetStateAsync(book, targetChoice, work, binding, providerIdentity, cancellationToken);
        if (targetId is not null && work is null)
            targetStateError = "The selected copy no longer exists. Choose a target again.";
        if (orphanLogIds?.Count > 0)
        {
            if (work is null)
            {
                var invalidOrphanPlan = TtsuMergePlanner.Plan(null, book) with
                {
                    Error = "Choose an existing copy before assigning unassigned logs.",
                    TargetReview = targetReview
                };
                return WithTargetFingerprint(invalidOrphanPlan, targetChoice, targetReview, null, null, selectedInstallment, providerIdentity);
            }
            var orphans = await context.ImmersionLogs.AsNoTracking().Where(x => orphanLogIds.Contains(x.Id)).ToListAsync(cancellationToken);
            if (orphans.Count != orphanLogIds.Distinct().Count() || orphans.Any(x => x.MediaWorkId is not null || !IsTtsu(x)))
            {
                var invalidOrphanPlan = TtsuMergePlanner.Plan(work, book) with
                {
                    Error = "An unassigned log changed. Review its assignment again.",
                    TargetReview = targetReview
                };
                return WithTargetFingerprint(invalidOrphanPlan, targetChoice, targetReview, work, binding, selectedInstallment, providerIdentity);
            }
            work.Logs.AddRange(orphans);
        }
        var plan = TtsuMergePlanner.Plan(work, book, resolutions, binding, progressResolution);
        if (orphanLogIds?.Count > 0)
        {
            var assigned = work!.Logs.Where(x => orphanLogIds.Contains(x.Id)).ToList();
            var characters = assigned.Sum(x => (long)x.CharactersRead);
            var minutes = assigned.Sum(x => x.TimeSpentMinutes);
            plan = plan with
            {
                CurrentCharacters = plan.CurrentCharacters - characters,
                CurrentMinutes = plan.CurrentMinutes - minutes,
                AssignedCharacters = characters,
                AssignedMinutes = minutes
            };
        }
        plan = plan with
        {
            Error = choiceError ?? targetStateError ?? plan.Error,
            TargetReview = targetReview
        };
        return WithTargetFingerprint(plan, targetChoice, targetReview, work, binding, selectedInstallment, providerIdentity);
    }

    public Task<TtsuImportReceipt?> GetReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        context.TtsuImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);

    public async Task<TtsuImportReceipt> ApplyAsync(Guid operationId, IReadOnlyList<TtsuImportRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || requests.Count == 0)
            throw new TtsuImportReviewRequiredException("Select at least one book to import.");
        var requestedChoices = requests.Select(EffectiveChoice).ToList();
        if (requestedChoices.Where(x => x.Intent == TtsuCopyIntent.ExistingCopy)
            .GroupBy(x => x.WorkId).Any(x => x.Count() > 1))
            throw new TtsuImportReviewRequiredException("Select each target only once per batch. Import separate source folders one at a time.");
        for (var first = 0; first < requests.Count; first++)
        {
            for (var second = first + 1; second < requests.Count; second++)
            {
                if (SourceSelectionsOverlap(requests[first].Book, requests[second].Book))
                {
                    throw new TtsuImportReviewRequiredException(
                        "The same TTSU source was selected more than once. Import each source folder only once per batch.");
                }
            }
        }
        if (requests.SelectMany(x => x.OrphanLogIds ?? []).GroupBy(x => x).Any(x => x.Count() > 1))
            throw new TtsuImportReviewRequiredException("Assign each unassigned log to only one book.");
        try
        {
            return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                var receipt = await GetReceiptAsync(operationId, cancellationToken);
                if (receipt is not null) return receipt;
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                receipt = new TtsuImportReceipt { Id = operationId, Books = requests.Count };
                var catalog = new MediaCatalogService(context);
                foreach (var request in requests)
                {
                    var targetChoice = EffectiveChoice(request);
                    // Legacy callers did not review catalogue identity. The Batch 4A
                    // contract opts in with TargetChoice and binds fresh provider
                    // identity evidence into that reviewed fingerprint.
                    var providerIdentity = request.TargetChoice is not null ? request.ProviderIdentity : null;
                    if (providerIdentity is not null && request.Metadata?.Selection is { } selectedMetadata &&
                        TtsuProviderIdentityHint.FromJiten(selectedMetadata) != providerIdentity)
                    {
                        throw new TtsuImportReviewRequiredException(
                            "The provider identity changed during confirmation. Review the import again.");
                    }
                    var plan = await PreviewTargetAsync(request.Book, targetChoice, request.Resolutions,
                        request.OrphanLogIds, cancellationToken, request.ProgressResolution, providerIdentity);
                    if (!plan.CanApply || plan.Fingerprint != request.ExpectedFingerprint)
                        throw new TtsuImportReviewRequiredException(plan.Error ?? "Statistics or choices changed. Review the refreshed preview before confirming.");
                    var isNewWork = targetChoice.Intent != TtsuCopyIntent.ExistingCopy;
                    var work = targetChoice.Intent switch
                    {
                        TtsuCopyIntent.NewInstallmentAndCopy => await catalog.CreateTrackedCopyAsync(
                            request.Book.Title.Trim(), MediaType.Book, cancellationToken: cancellationToken),
                        TtsuCopyIntent.NewCopyUnderExistingInstallment => await catalog.CreateTrackedCopyAsync(
                            request.Book.Title.Trim(), MediaType.Book,
                            installmentId: targetChoice.InstallmentId,
                            cancellationToken: cancellationToken),
                        TtsuCopyIntent.ExistingCopy => await context.MediaWorks.Include(x => x.Logs).Include(x => x.MediaSeries)
                            .Include(x => x.MediaInstallment).ThenInclude(x => x!.MediaSeries)
                            .SingleAsync(x => x.Id == targetChoice.WorkId, cancellationToken),
                        _ => throw new TtsuImportReviewRequiredException("The copy choice is invalid. Review the import again.")
                    };
                    if (!isNewWork)
                    {
                        await catalog.EnsureInstallmentAsync(work, cancellationToken);
                    }
                    if (request.OrphanLogIds?.Count > 0)
                        work.Logs.AddRange(await context.ImmersionLogs.Where(x => request.OrphanLogIds.Contains(x.Id)).ToListAsync(cancellationToken));

                    var hasFolderCover = !string.IsNullOrWhiteSpace(request.Book.CoverImage);
                    if (hasFolderCover && (isNewWork || !work.IsCoverProtected))
                    {
                        try
                        {
                            work.ApplyTtsuCover(request.Book.CoverImage);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                        }
                    }

                    if (request.Metadata is not null)
                    {
                        if (request.Metadata.Selection is { } selection)
                        {
                            if (isNewWork)
                            {
                                await LinkJitenForImportAsync(catalog, work, selection, cancellationToken);
                                receipt.MetadataLinks++;

                                if (!hasFolderCover && request.Metadata.EffectiveCover is { } coverSelection)
                                {
                                    try
                                    {
                                        if (coverSelection.Provider == Models.Covers.ExternalCoverProvider.OpenLibrary)
                                        {
                                            work.ApplyOpenLibraryCover(coverSelection.CoverUrl, coverSelection.ProviderItemId);
                                        }
                                        else
                                        {
                                            work.ApplyGoogleBooksCover(coverSelection.CoverUrl, coverSelection.ProviderItemId);
                                        }
                                    }
                                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                                    {
                                        // A cover failure must never fail the transaction
                                    }
                                }
                            }
                            else if (!work.HasJitenLink && !work.HasCover)
                            {
                                await LinkJitenForImportAsync(catalog, work, selection, cancellationToken);
                                receipt.MetadataLinks++;

                                if (!hasFolderCover && request.Metadata.EffectiveCover is { } coverSelection)
                                {
                                    try
                                    {
                                        if (coverSelection.Provider == Models.Covers.ExternalCoverProvider.OpenLibrary)
                                        {
                                            work.ApplyOpenLibraryCover(coverSelection.CoverUrl, coverSelection.ProviderItemId);
                                        }
                                        else
                                        {
                                            work.ApplyGoogleBooksCover(coverSelection.CoverUrl, coverSelection.ProviderItemId);
                                        }
                                    }
                                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                                    {
                                        // A cover failure must never fail the transaction
                                    }
                                }
                            }
                            else
                            {
                                receipt.MetadataSkips++;
                            }
                        }
                        else
                        {
                            receipt.MetadataSkips++;
                        }
                    }
                    var binding = await context.TtsuBindings.SingleOrDefaultAsync(x => x.MediaWorkId == work.Id, cancellationToken);
                    if (binding is null)
                    {
                        binding = new TtsuBinding { MediaWorkId = work.Id };
                        context.TtsuBindings.Add(binding);
                    }
                    binding.OriginalTitle = TtsuBookImporter.NormalizeTitle(request.Book.Title);
                    binding.FolderHint = request.Book.FolderHint;
                    binding.Version = Guid.NewGuid();
                    var acceptedProgressUpdate = false;
                    if (plan.Progress.Accepted is { } acceptedProgress)
                    {
                        binding.CurrentCharacterPosition = acceptedProgress.CharacterPosition;
                        binding.ProgressFraction = acceptedProgress.ProgressFraction;
                        binding.ProgressRevision = acceptedProgress.Revision;
                        binding.ProgressExporterVersion = acceptedProgress.ExporterVersion;
                        binding.ProgressDatabaseVersion = acceptedProgress.DatabaseVersion;
                        binding.TotalInferenceKind = acceptedProgress.InferenceKind;
                        receipt.ProgressUpdates++;
                        acceptedProgressUpdate = true;

                        if (acceptedProgress.InferredTotalCharacters is int inferredTotal &&
                            work.TtsuCharacterCount != inferredTotal)
                        {
                            work.UpdateTtsuCharacterCount(inferredTotal);
                            receipt.CharacterTotalUpdates++;
                        }
                    }
                    // TTSU stores a completed bookmark at total - 1 with progress 1.
                    // Completion is monotonic here: an incomplete bookmark must not erase
                    // a status the user explicitly marked as completed.
                    var resultingProgress = plan.Progress.Accepted ?? plan.Progress.Existing;
                    if (request.Book.ProgressEntries.Count > 0 &&
                        resultingProgress?.ProgressFraction >= 1d && !work.IsCompleted)
                    {
                        work.IsCompleted = true;
                        if (!acceptedProgressUpdate)
                        {
                            // An unchanged bookmark can still repair a work imported before
                            // completion propagation was introduced.
                            receipt.ProgressUpdates++;
                        }
                    }
                    foreach (var day in plan.Days)
                    {
                        var retained = work.Logs.SingleOrDefault(x => x.Id == day.RetainedLogId);
                        foreach (var duplicate in day.Existing.Where(x => x.Id != day.RetainedLogId))
                        {
                            var log = work.Logs.Single(x => x.Id == duplicate.Id);
                            context.ImmersionLogs.Remove(log);
                            work.Logs.Remove(log);
                        }
                        if (retained is null && day.Accepted is not null)
                        {
                            retained = new ImmersionLog { Date = day.Date, MediaWorkId = work.Id };
                            work.Logs.Add(retained);
                            context.ImmersionLogs.Add(retained);
                        }
                        if (retained is not null)
                        {
                            retained.MediaWorkId = work.Id;
                            retained.TtsuBindingId = work.Id;
                            retained.Source = "ttsu";
                            if (day.Accepted is not null)
                            {
                                retained.CharactersRead = day.Accepted.Characters;
                                retained.TimeSpentMinutes = day.Accepted.Minutes;
                                retained.SourceRevision = day.Accepted.Revision;
                            }
                        }
                    }
                    receipt.AddedDays += plan.Count(TtsuDayAction.Added);
                    receipt.UpdatedDays += plan.Count(TtsuDayAction.Updated);
                    receipt.UnchangedDays += plan.Count(TtsuDayAction.Unchanged);
                    receipt.StaleDays += plan.Count(TtsuDayAction.Stale);
                }
                context.TtsuImportReceipts.Add(receipt);
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return receipt;
            });
        }
        catch (Exception exception) when (IsCollision(exception))
        {
            context.ChangeTracker.Clear();
            var committed = await GetReceiptAsync(operationId, cancellationToken);
            if (committed is not null) return committed;
            throw new TtsuImportReviewRequiredException("Another import changed these statistics. Refresh the review and try again.");
        }
    }

    private static async Task LinkJitenForImportAsync(
        MediaCatalogService catalog,
        MediaWork work,
        JitenMediaSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            await catalog.LinkToJitenAsync(work, selection, JitenTitleChoice.KeepCurrent, cancellationToken);
        }
        catch (MediaCatalogConflictException exception)
        {
            throw new TtsuImportReviewRequiredException(exception.Message);
        }
    }

    private static TtsuImportTargetChoice EffectiveChoice(TtsuImportRequest request) =>
        request.TargetChoice ?? (request.TargetId is Guid workId
            ? TtsuImportTargetChoice.ExistingCopy(workId)
            : TtsuImportTargetChoice.NewInstallment());

    private static string? ValidateTargetChoice(TtsuImportTargetChoice choice) => choice.Intent switch
    {
        TtsuCopyIntent.ExistingCopy when choice.WorkId is null || choice.WorkId == Guid.Empty || choice.InstallmentId is not null =>
            "Choose one valid existing copy.",
        TtsuCopyIntent.NewCopyUnderExistingInstallment when choice.InstallmentId is null || choice.InstallmentId == Guid.Empty || choice.WorkId is not null =>
            "Choose one valid catalogue installment for the new copy.",
        TtsuCopyIntent.NewInstallmentAndCopy when choice.WorkId is not null || choice.InstallmentId is not null =>
            "A new installment choice cannot also name an existing copy or installment.",
        _ when !Enum.IsDefined(choice.Intent) => "The copy choice is invalid.",
        _ => null
    };

    private async Task<string?> ValidateTargetStateAsync(
        TtsuBookContainer book,
        TtsuImportTargetChoice choice,
        MediaWork? work,
        TtsuBinding? binding,
        TtsuProviderIdentityHint? providerIdentity,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = TtsuBookImporter.NormalizeTitle(book.Title);
        if (choice.Intent == TtsuCopyIntent.ExistingCopy)
        {
            if (work is null)
            {
                return "The selected copy no longer exists. Choose a target again.";
            }
            if (work.MediaType != MediaType.Book)
            {
                return "Choose a book copy as the import target.";
            }
            if (binding is not null && !SourceMatches(binding, normalizedTitle, book.FolderHint))
            {
                return "That copy already belongs to a different TTSU source. Choose another copy or create a separate copy.";
            }
        }

        Guid? selectedInstallmentId = choice.Intent switch
        {
            TtsuCopyIntent.ExistingCopy => work?.MediaInstallmentId,
            TtsuCopyIntent.NewCopyUnderExistingInstallment => choice.InstallmentId,
            _ => null
        };
        if (choice.Intent == TtsuCopyIntent.NewCopyUnderExistingInstallment)
        {
            var installment = await context.MediaInstallments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == choice.InstallmentId, cancellationToken);
            if (installment is null)
            {
                return "The selected installment no longer exists. Choose a target again.";
            }
            if (installment.MediaType != MediaType.Book)
            {
                return "Choose a book installment for the new copy.";
            }
        }

        if (providerIdentity is not null)
        {
            var providerInstallmentId = await context.InstallmentProviderIdentities.AsNoTracking()
                .Where(identity => identity.Provider == providerIdentity.Provider &&
                    identity.NormalizedKey == providerIdentity.NormalizedKey)
                .Select(identity => (Guid?)identity.MediaInstallmentId)
                .SingleOrDefaultAsync(cancellationToken);
            if (providerInstallmentId is Guid exactId)
            {
                if (choice.Intent == TtsuCopyIntent.NewInstallmentAndCopy)
                {
                    return "That provider item already belongs to a catalogue installment. Choose that installment instead of creating a duplicate.";
                }
                if (selectedInstallmentId != exactId)
                {
                    return "The selected provider item belongs to a different catalogue installment. Review the copy assignment.";
                }
            }
        }

        return null;
    }

    private static TtsuImportPlan WithTargetFingerprint(
        TtsuImportPlan plan,
        TtsuImportTargetChoice targetChoice,
        TtsuImportTargetReview targetReview,
        MediaWork? work,
        TtsuBinding? binding,
        TtsuInstallmentTargetCandidate? selectedInstallment,
        TtsuProviderIdentityHint? providerIdentity)
    {
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                plan.Fingerprint,
                TargetChoice = targetChoice,
                TargetReview = targetReview,
                TargetState = work is null ? null : new
                {
                    work.Id,
                    work.Version,
                    work.MediaInstallmentId,
                    InstallmentVersion = work.MediaInstallment?.Version,
                    SeriesId = work.MediaInstallment?.MediaSeriesId ?? work.MediaSeriesId
                },
                BindingState = binding is null ? null : new
                {
                    binding.MediaWorkId,
                    binding.OriginalTitle,
                    binding.FolderHint,
                    binding.Version
                },
                SelectedInstallmentState = selectedInstallment,
                ProviderIdentity = providerIdentity
            })));
        return plan with { Fingerprint = fingerprint, TargetReview = targetReview };
    }

    private static bool SourceMatches(TtsuBinding binding, string normalizedTitle, string? folderHint) =>
        binding.OriginalTitle == normalizedTitle &&
        (binding.FolderHint == folderHint || binding.FolderHint is null || folderHint is null);

    private static bool SourceSelectionsOverlap(TtsuBookContainer first, TtsuBookContainer second) =>
        TtsuBookImporter.NormalizeTitle(first.Title) == TtsuBookImporter.NormalizeTitle(second.Title) &&
        (first.FolderHint == second.FolderHint || first.FolderHint is null || second.FolderHint is null);

    private static string? LegacyJitenKey(int? deckId, int? subdeckId) => deckId switch
    {
        null => null,
        int parent when subdeckId is int child => $"subdeck:{parent}:{child}",
        int parent => $"deck:{parent}"
    };

    private async Task<TtsuInstallmentTargetCandidate?> GetInstallmentStateAsync(
        Guid installmentId,
        CancellationToken cancellationToken)
    {
        var row = await context.MediaInstallments.AsNoTracking()
            .Where(installment => installment.Id == installmentId)
            .Select(installment => new
            {
                installment.Id,
                Title = installment.TitleOverride ?? installment.CanonicalTitle ?? installment.LegacyTitle ?? string.Empty,
                installment.Version,
                installment.MediaSeriesId,
                CopyIds = installment.Copies.Select(copy => copy.Id).ToList(),
                ProviderKeys = installment.ProviderIdentities
                    .Select(identity => identity.Provider + ":" + identity.NormalizedKey).ToList()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var boundCopyCount = await context.TtsuBindings.AsNoTracking()
            .CountAsync(binding => row.CopyIds.Contains(binding.MediaWorkId), cancellationToken);
        return new(
            row.Id,
            row.Title,
            row.Version,
            row.MediaSeriesId,
            row.CopyIds.Count,
            row.CopyIds.Count - boundCopyCount,
            row.ProviderKeys.Order(StringComparer.Ordinal).ToList());
    }

    private static bool IsTtsu(ImmersionLog log) => string.Equals(log.Source, "ttsu", StringComparison.OrdinalIgnoreCase);
    private static bool IsCollision(Exception exception) => exception is DbUpdateConcurrencyException ||
        exception is PostgresException { SqlState: "23505" or "40001" or "40P01" } ||
        exception is SqliteException { SqliteErrorCode: 5 or 6 or 19 } ||
        exception.InnerException is not null && IsCollision(exception.InnerException);
}
