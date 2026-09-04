using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;

namespace Kiseki.Core.Services;

public static class TtsuBookImporter
{
    public static MediaWork CreateMediaWork(TtsuBookContainer book)
    {
        ValidateBook(book);

        var work = new MediaWork(book.Title.Trim(), mediaType: MediaType.Book);
        foreach (var log in MapLatestDailyLogs(book))
        {
            work.Logs.Add(log);
        }

        return work;
    }

    public static TtsuMergeResult MergeInto(MediaWork work, TtsuBookContainer book)
    {
        ArgumentNullException.ThrowIfNull(work);
        ValidateBook(book);

        var existingLogsByDate = work.Logs
            .Where(log => string.Equals(log.Source, "ttsu", StringComparison.OrdinalIgnoreCase))
            .GroupBy(log => log.Date)
            .ToDictionary(group => group.Key, group => group.First());

        var added = 0;
        var updated = 0;
        var addedLogs = new List<ImmersionLog>();

        foreach (var incomingLog in MapLatestDailyLogs(book))
        {
            if (existingLogsByDate.TryGetValue(incomingLog.Date, out var existingLog))
            {
                existingLog.CharactersRead = incomingLog.CharactersRead;
                existingLog.TimeSpentMinutes = incomingLog.TimeSpentMinutes;
                existingLog.Source = incomingLog.Source;
                updated++;
                continue;
            }

            work.Logs.Add(incomingLog);
            existingLogsByDate.Add(incomingLog.Date, incomingLog);
            addedLogs.Add(incomingLog);
            added++;
        }

        return new TtsuMergeResult(added, updated, addedLogs);
    }

    public static string NormalizeTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return string.Join(
                ' ',
                title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private static IReadOnlyList<ImmersionLog> MapLatestDailyLogs(TtsuBookContainer book)
    {
        return book.Entries
            .Select((entry, index) => new { Entry = entry, Index = index })
            .GroupBy(item => item.Entry.DateKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Entry.LastStatisticModified)
                .ThenByDescending(item => item.Index)
                .First()
                .Entry
                .ToImmersionLog())
            .OrderBy(log => log.Date)
            .ToList();
    }

    private static void ValidateBook(TtsuBookContainer book)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (string.IsNullOrWhiteSpace(book.Title))
        {
            throw new ArgumentException("A TTSU book title is required.", nameof(book));
        }
    }
}

public sealed record TtsuMergeResult(
    int AddedSessions,
    int UpdatedSessions,
    IReadOnlyList<ImmersionLog> AddedLogs);
