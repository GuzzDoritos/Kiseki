using Kiseki.Core.DTOs;
using Kiseki.Core.Services;

namespace Kiseki.Web.Models;

public sealed class TtsuBookPreviewViewModel
{
    public required Guid BookKey { get; init; }
    public required string Title { get; init; }
    public required int EntryCount { get; init; }
    public required int CharactersRead { get; init; }
    public required double ReadingTimeSeconds { get; init; }
    public required DateOnly? FirstReadDate { get; init; }
    public required DateOnly? LastReadDate { get; init; }
    public required IReadOnlyList<TtsuEntryPreviewViewModel> Entries { get; init; }
    public Guid? ExistingMediaWorkId { get; init; }

    public bool ExistsInLibrary => ExistingMediaWorkId.HasValue;

    public string ReadingTimeLabel => FormatDuration(ReadingTimeSeconds);

    public string DateRangeLabel => (FirstReadDate, LastReadDate) switch
    {
        (null, null) => "No reading dates",
        ({ } first, { } last) when first == last => first.ToString("MMM d, yyyy"),
        ({ } first, { } last) => $"{first:MMM d, yyyy} – {last:MMM d, yyyy}",
        _ => "No reading dates"
    };

    public static TtsuBookPreviewViewModel Create(
        Guid bookKey,
        TtsuBookContainer book,
        Guid? existingMediaWorkId)
    {
        var entries = book.Entries
            .Select(entry => new TtsuEntryPreviewViewModel(
                TtsuSessionMapper.TryParseDate(entry.DateKey, out var date) ? date : null,
                entry.CharactersRead,
                entry.ReadingTime))
            .OrderBy(entry => entry.Date)
            .ToList();

        return new TtsuBookPreviewViewModel
        {
            BookKey = bookKey,
            Title = book.Title,
            EntryCount = entries.Count,
            CharactersRead = entries.Sum(entry => entry.CharactersRead),
            ReadingTimeSeconds = entries.Sum(entry => entry.ReadingTimeSeconds),
            FirstReadDate = entries.FirstOrDefault()?.Date,
            LastReadDate = entries.LastOrDefault()?.Date,
            Entries = entries,
            ExistingMediaWorkId = existingMediaWorkId
        };
    }

    public static string FormatDuration(double seconds)
    {
        var totalMinutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return hours > 0 ? $"{hours:N0}h {minutes}m" : $"{minutes}m";
    }
}

public sealed record TtsuEntryPreviewViewModel(
    DateOnly? Date,
    int CharactersRead,
    double ReadingTimeSeconds)
{
    public string ReadingTimeLabel => TtsuBookPreviewViewModel.FormatDuration(ReadingTimeSeconds);
}

public sealed class TtsuBookSelectionInput
{
    public Guid BookKey { get; set; }
    public bool Selected { get; set; }
    public TtsuImportMode Mode { get; set; }
}

public enum TtsuImportMode
{
    Merge,
    Create
}
