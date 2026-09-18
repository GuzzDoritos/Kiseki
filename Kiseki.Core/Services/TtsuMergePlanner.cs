using System.Security.Cryptography;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public static class TtsuMergePlanner
{
    public static TtsuImportPlan Plan(MediaWork? work, TtsuBookContainer book,
        IReadOnlyDictionary<DateOnly, string>? resolutions = null, TtsuBinding? binding = null,
        string? progressResolution = null)
    {
        var snapshots = TtsuStatisticsNormalizer.Normalize(book);
        var progress = PlanProgress(work, book, binding, progressResolution);
        var logs = work?.Logs.Where(log => string.Equals(log.Source, "ttsu", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var dates = snapshots.Select(x => x.Date).Union(logs.Select(x => x.Date)).Order().ToList();
        var days = new List<TtsuDayPlan>();
        foreach (var date in dates)
        {
            var existing = logs.Where(x => x.Date == date).OrderBy(x => x.Id)
                .Select(x => new TtsuStoredDay(x.Id, x.CharactersRead, x.TimeSpentMinutes, x.SourceRevision)).ToList();
            var incoming = snapshots.Where(x => x.Date == date).ToList();
            var current = existing.Count == 1 ? existing[0] : null;
            var next = incoming.Count == 1 ? incoming[0] : null;
            string? reason = existing.Count > 1 ? "Duplicate stored days: select the daily total to keep." :
                incoming.Count > 1 ? "Incoming revisions disagree: select the daily total to use." :
                current is not null && next is not null && (current.Revision is null || next.Revision is null)
                    && (!TtsuStatisticsNormalizer.SameValues(current.Characters, current.Minutes, next) || current.Revision is null && next.Revision is not null)
                    ? "Source revision is unknown: review this baseline." :
                current is not null && next is not null && current.Revision == next.Revision &&
                    !TtsuStatisticsNormalizer.SameValues(current.Characters, current.Minutes, next)
                    ? "The same revision contains different totals." : null;
            TtsuDayAction action;
            TtsuDailySnapshot? accepted = null;
            Guid? retained = current?.Id;
            if (reason is not null)
            {
                var choice = resolutions?.GetValueOrDefault(date);
                if (choice?.StartsWith("incoming:", StringComparison.Ordinal) == true &&
                    int.TryParse(choice[9..], out var index) && index >= 0 && index < incoming.Count)
                {
                    accepted = incoming[index];
                    // Keep the row ID on replacement, including an explicitly resolved duplicate.
                    retained = existing.FirstOrDefault()?.Id;
                    action = existing.Count == 0 ? TtsuDayAction.Added : TtsuDayAction.Updated;
                    if (current is not null && TtsuStatisticsNormalizer.SameValues(current.Characters, current.Minutes, accepted))
                        action = TtsuDayAction.Unchanged;
                }
                else if (choice?.StartsWith("keep:", StringComparison.Ordinal) == true && Guid.TryParse(choice[5..], out var id) && existing.Any(x => x.Id == id))
                {
                    retained = id;
                    action = existing.Count > 1 ? TtsuDayAction.Updated : TtsuDayAction.Unchanged;
                }
                else action = TtsuDayAction.Conflict;
            }
            else if (next is null) action = TtsuDayAction.Unchanged;
            else if (current is null) { accepted = next; action = TtsuDayAction.Added; }
            else if (next.Revision < current.Revision) action = TtsuDayAction.Stale;
            else
            {
                action = TtsuStatisticsNormalizer.SameValues(current.Characters, current.Minutes, next) ? TtsuDayAction.Unchanged : TtsuDayAction.Updated;
                // Missing source metadata must not erase an established revision.
                if (next.Revision is not null || current.Revision is null) accepted = next;
            }
            days.Add(new(date, existing, incoming, action, accepted, retained, reason));
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Target = work?.Id,
            WorkTitle = work?.Title,
            book.Title,
            book.FolderHint,
            binding,
            Logs = work?.Logs.OrderBy(x => x.Id).Select(x => new { x.Id, x.MediaWorkId, x.Date, x.CharactersRead, x.TimeSpentMinutes, x.Source, x.SourceRevision, x.TtsuBindingId }),
            Days = days,
            Progress = progress,
            work?.TtsuCharacterCount,
            work?.ManualCharacterCountOverride,
            work?.IsCompleted
        })));
        return new(work?.Id, work?.Title ?? book.Title, fingerprint, days,
            work?.Logs.Sum(x => (long)x.CharactersRead) ?? 0, work?.Logs.Sum(x => x.TimeSpentMinutes) ?? 0,
            work is not null && work.MediaType != MediaType.Book ? "Choose a book as the import target." : null,
            progress);
    }

    private static TtsuProgressPlan PlanProgress(MediaWork? work, TtsuBookContainer book,
        TtsuBinding? binding, string? resolution)
    {
        var incoming = TtsuProgressNormalizer.Normalize(book);
        var existing = binding?.CurrentCharacterPosition is int position &&
            binding.ProgressFraction is double fraction && binding.TotalInferenceKind is { } inferenceKind
            ? new TtsuProgressSnapshot(position, fraction, binding.ProgressRevision,
                work?.TtsuCharacterCount, inferenceKind, binding.ProgressExporterVersion,
                binding.ProgressDatabaseVersion)
            : null;

        if (incoming.Count == 0)
        {
            return new(existing, incoming, TtsuProgressAction.None, null, null);
        }

        var next = incoming.Count == 1 ? incoming[0] : null;
        string? reason = incoming.Count > 1
            ? "Incoming bookmark revisions disagree: select the reading position to use."
            : existing is not null && next is not null &&
                (existing.Revision is null || next.Revision is null) &&
                (!SameProgressValues(existing, next) || existing.Revision is null && next.Revision is not null)
                ? "Bookmark revision is unknown: review this progress baseline."
                : existing is not null && next is not null && existing.Revision == next.Revision &&
                    !SameProgressValues(existing, next)
                    ? "The same bookmark revision contains different progress values."
                    : null;

        if (reason is not null)
        {
            if (resolution?.StartsWith("incoming:", StringComparison.Ordinal) == true &&
                int.TryParse(resolution[9..], out var index) && index >= 0 && index < incoming.Count)
            {
                var accepted = incoming[index];
                var action = existing is null ? TtsuProgressAction.Added :
                    SameProgressValues(existing, accepted) ? TtsuProgressAction.Unchanged : TtsuProgressAction.Updated;
                return new(existing, incoming, action, accepted, reason);
            }

            if (resolution == "keep" && existing is not null)
            {
                return new(existing, incoming, TtsuProgressAction.Unchanged, null, reason);
            }

            return new(existing, incoming, TtsuProgressAction.Conflict, null, reason);
        }

        if (next is null)
        {
            return new(existing, incoming, TtsuProgressAction.None, null, null);
        }
        if (existing is null)
        {
            return new(null, incoming, TtsuProgressAction.Added, next, null);
        }
        if (next.Revision < existing.Revision)
        {
            return new(existing, incoming, TtsuProgressAction.Stale, null, null);
        }

        var nextAction = SameProgressValues(existing, next)
            ? TtsuProgressAction.Unchanged
            : TtsuProgressAction.Updated;
        var revisionAdvanced = next.Revision is long nextRevision &&
            (existing.Revision is null || nextRevision > existing.Revision.Value);
        var acceptedNext = (next.Revision is not null || existing.Revision is null) &&
            (nextAction == TtsuProgressAction.Updated || revisionAdvanced)
            ? next
            : null;
        return new(existing, incoming, nextAction, acceptedNext, null);
    }

    private static bool SameProgressValues(TtsuProgressSnapshot first, TtsuProgressSnapshot second) =>
        first.CharacterPosition == second.CharacterPosition &&
        Math.Abs(first.ProgressFraction - second.ProgressFraction) <= 1e-12 &&
        first.InferredTotalCharacters == second.InferredTotalCharacters &&
        first.InferenceKind == second.InferenceKind;
}
