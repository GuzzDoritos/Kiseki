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

    public async Task<TtsuMatch> MatchAsync(TtsuBookContainer book, CancellationToken cancellationToken = default)
    {
        var title = TtsuBookImporter.NormalizeTitle(book.Title);
        var bindings = await context.TtsuBindings.AsNoTracking().ToListAsync(cancellationToken);
        var matches = bindings.Where(x => x.OriginalTitle == title &&
            (x.FolderHint == book.FolderHint || x.FolderHint is null || book.FolderHint is null)).ToList();
        if (matches.Count == 1) return new(matches[0].MediaWorkId, "Previously confirmed TTSU book");
        if (matches.Count > 1) return new(null, "Multiple source matches. Choose the target explicitly.", IsAmbiguous: true);
        var candidates = (await GetTargetsAsync(cancellationToken)).Where(x => TtsuBookImporter.NormalizeTitle(x.Title) == title).ToList();
        return candidates.Count switch
        {
            1 => new(candidates[0].Id, "Matching library title; confirm this target"),
            > 1 => new(null, "Multiple books have this title. Choose the target explicitly.", IsAmbiguous: true),
            _ => new(null, "No confirmed match. Choose an existing book or create a new one.")
        };
    }

    public async Task<IReadOnlyList<ImmersionLog>> GetOrphansAsync(CancellationToken cancellationToken = default) =>
        (await context.ImmersionLogs.AsNoTracking().Where(x => x.MediaWorkId == null).ToListAsync(cancellationToken))
            .Where(IsTtsu).OrderBy(x => x.Date).ToList();

    public async Task<TtsuImportPlan> PreviewAsync(TtsuBookContainer book, Guid? targetId,
        IReadOnlyDictionary<DateOnly, string>? resolutions = null, IReadOnlyList<Guid>? orphanLogIds = null,
        CancellationToken cancellationToken = default, string? progressResolution = null)
    {
        var work = targetId is null ? null : await context.MediaWorks.AsNoTracking()
            .Include(x => x.Logs).Include(x => x.MediaSeries).SingleOrDefaultAsync(x => x.Id == targetId, cancellationToken);
        var binding = targetId is null ? null : await context.TtsuBindings.AsNoTracking().SingleOrDefaultAsync(x => x.MediaWorkId == targetId, cancellationToken);
        if (targetId is not null && work is null)
            return TtsuMergePlanner.Plan(null, book) with { Error = "The selected book no longer exists. Choose a target again." };
        if (orphanLogIds?.Count > 0)
        {
            if (work is null) return TtsuMergePlanner.Plan(null, book) with { Error = "Choose an existing book before assigning unassigned logs." };
            var orphans = await context.ImmersionLogs.AsNoTracking().Where(x => orphanLogIds.Contains(x.Id)).ToListAsync(cancellationToken);
            if (orphans.Count != orphanLogIds.Distinct().Count() || orphans.Any(x => x.MediaWorkId is not null || !IsTtsu(x)))
                return TtsuMergePlanner.Plan(work, book) with { Error = "An unassigned log changed. Review its assignment again." };
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
        // Read source matching inside the commit transaction too: concurrent first imports
        // must not both turn the same unmatched source into a new work.
        var match = await MatchAsync(book, cancellationToken);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { plan.Fingerprint, match })));
        return plan with { Fingerprint = fingerprint };
    }

    public Task<TtsuImportReceipt?> GetReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        context.TtsuImportReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);

    public async Task<TtsuImportReceipt> ApplyAsync(Guid operationId, IReadOnlyList<TtsuImportRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || requests.Count == 0)
            throw new TtsuImportReviewRequiredException("Select at least one book to import.");
        if (requests.Where(x => x.TargetId.HasValue).GroupBy(x => x.TargetId).Any(x => x.Count() > 1))
            throw new TtsuImportReviewRequiredException("Select each target only once per batch. Import separate source folders one at a time.");
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
                foreach (var request in requests)
                {
                    var plan = await PreviewAsync(request.Book, request.TargetId, request.Resolutions,
                        request.OrphanLogIds, cancellationToken, request.ProgressResolution);
                    if (!plan.CanApply || plan.Fingerprint != request.ExpectedFingerprint)
                        throw new TtsuImportReviewRequiredException(plan.Error ?? "Statistics or choices changed. Review the refreshed preview before confirming.");
                    var work = request.TargetId is null ? new MediaWork(request.Book.Title.Trim()) :
                        await context.MediaWorks.Include(x => x.Logs).Include(x => x.MediaSeries).SingleAsync(x => x.Id == request.TargetId, cancellationToken);
                    if (request.TargetId is null) context.MediaWorks.Add(work);
                    if (request.OrphanLogIds?.Count > 0)
                        work.Logs.AddRange(await context.ImmersionLogs.Where(x => request.OrphanLogIds.Contains(x.Id)).ToListAsync(cancellationToken));
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

    private static bool IsTtsu(ImmersionLog log) => string.Equals(log.Source, "ttsu", StringComparison.OrdinalIgnoreCase);
    private static bool IsCollision(Exception exception) => exception is DbUpdateConcurrencyException ||
        exception is PostgresException { SqlState: "23505" or "40001" or "40P01" } ||
        exception is SqliteException { SqliteErrorCode: 5 or 6 or 19 } ||
        exception.InnerException is not null && IsCollision(exception.InnerException);
}
