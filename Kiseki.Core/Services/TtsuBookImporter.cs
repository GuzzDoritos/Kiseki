using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public static class TtsuBookImporter
{
    public static MediaWork CreateMediaWork(TtsuBookContainer book)
    {
        var work = new MediaWork(book.Title.Trim(), mediaType: MediaType.Book);
        MergeInto(work, book);
        return work;
    }

    public static TtsuMergeResult MergeInto(MediaWork work, TtsuBookContainer book)
    {
        ArgumentNullException.ThrowIfNull(work);
        var plan = TtsuMergePlanner.Plan(work, book);
        if (!plan.CanApply) throw new TtsuImportReviewRequiredException("Review conflicting statistics before merging.");
        if (plan.Progress.Accepted?.InferredTotalCharacters is int inferredTotal)
        {
            work.UpdateTtsuCharacterCount(inferredTotal);
        }
        var resultingProgress = plan.Progress.Accepted ?? plan.Progress.Existing;
        if (book.ProgressEntries.Count > 0 && resultingProgress?.ProgressFraction >= 1d)
        {
            work.IsCompleted = true;
        }
        var added = new List<ImmersionLog>();
        foreach (var day in plan.Days.Where(x => x.Accepted is not null))
        {
            var log = work.Logs.SingleOrDefault(x => x.Id == day.RetainedLogId);
            if (log is null)
            {
                log = new ImmersionLog { Date = day.Date };
                work.Logs.Add(log);
                added.Add(log);
            }
            log.CharactersRead = day.Accepted!.Characters;
            log.TimeSpentMinutes = day.Accepted.Minutes;
            log.SourceRevision = day.Accepted.Revision;
        }
        return new(added.Count, plan.Count(TtsuDayAction.Updated), added);
    }

    public static string NormalizeTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}

public sealed record TtsuMergeResult(int AddedSessions, int UpdatedSessions, IReadOnlyList<ImmersionLog> AddedLogs);
