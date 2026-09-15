using Kiseki.Core.DTOs;
using Kiseki.Core.Models;

namespace Kiseki.Core.Services;

public static class TtsuStatisticsNormalizer
{
    // A microsecond expressed in minutes: accommodates legacy seconds/minutes round trips.
    public static bool SameValues(int characters, double minutes, TtsuDailySnapshot other) =>
        characters == other.Characters && Math.Abs(minutes - other.Minutes) <= 1d / 60_000_000;

    public static IReadOnlyList<TtsuDailySnapshot> Normalize(TtsuBookContainer book)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (string.IsNullOrWhiteSpace(book.Title) || book.Entries is null)
            throw new InvalidDataException("A book title and statistics entry list are required.");
        var snapshots = new List<TtsuDailySnapshot>();
        foreach (var entry in book.Entries)
        {
            if (entry is null || !TtsuSessionMapper.TryParseDate(entry.DateKey, out var date))
                throw new InvalidDataException("Each statistic needs a valid yyyy-MM-dd date.");
            if (entry.CharactersRead < 0 || !double.IsFinite(entry.ReadingTime) || entry.ReadingTime < 0 || entry.LastStatisticModified < 0)
                throw new InvalidDataException("Statistics must contain non-negative, finite values and revisions.");
            if (!string.IsNullOrWhiteSpace(entry.Title) && TtsuBookImporter.NormalizeTitle(entry.Title) != TtsuBookImporter.NormalizeTitle(book.Title))
                throw new InvalidDataException("A statistics file contains different book titles. Export each book separately.");
            snapshots.Add(new(date, entry.CharactersRead, entry.ReadingTime / 60d,
                entry.LastStatisticModified > 0 ? entry.LastStatisticModified : null));
        }
        return snapshots.GroupBy(x => x.Date).OrderBy(x => x.Key).SelectMany(group =>
        {
            var latest = group.Max(x => x.Revision);
            // Unknown revisions cannot safely be ordered against known ones.
            return group.Where(x => x.Revision is null || x.Revision == latest).Distinct()
                .OrderBy(x => x.Revision).ThenBy(x => x.Characters).ThenBy(x => x.Minutes);
        }).ToList();
    }

    public static IReadOnlyList<TtsuBookContainer> CombineFiles(IEnumerable<TtsuBookContainer> books) =>
        books.GroupBy(book => (book.FolderHint, Title: TtsuBookImporter.NormalizeTitle(book.Title)))
            .Select(group => new TtsuBookContainer
            {
                Title = group.First().Title,
                FolderHint = group.Key.FolderHint,
                Entries = group.SelectMany(book => book.Entries).ToList(),
                ProgressEntries = group.SelectMany(book => book.ProgressEntries).ToList()
            }).ToList();
}
