using Kiseki.Core.DTOs;
using Kiseki.Core.Services;

namespace Kiseki.Tests;

public sealed class TtsuBookImporterTests
{
    [Fact]
    public void CreateMediaWork_CollapsesRepeatedDatesToTheLatestStatistic()
    {
        var book = Book(
            Entry("2026-08-05", 100, modified: 1),
            Entry("2026-08-05", 250, modified: 2),
            Entry("2026-08-06", 300, modified: 3));

        var work = TtsuBookImporter.CreateMediaWork(book);

        Assert.Equal("Test Book", work.Title);
        Assert.Equal(2, work.Logs.Count);
        Assert.Equal(550, work.CurrentCharactersRead);
    }

    [Fact]
    public void MergeInto_UpdatesExistingDatesAndAddsNewDates()
    {
        var work = TtsuBookImporter.CreateMediaWork(Book(Entry("2026-08-05", 100, modified: 1)));
        var updatedBook = Book(
            Entry("2026-08-05", 240, modified: 2),
            Entry("2026-08-06", 300, modified: 3));

        var result = TtsuBookImporter.MergeInto(work, updatedBook);

        Assert.Equal(1, result.AddedSessions);
        Assert.Equal(1, result.UpdatedSessions);
        Assert.Equal(2, work.Logs.Count);
        Assert.Equal(540, work.CurrentCharactersRead);
    }

    [Fact]
    public void NormalizeTitle_IgnoresCaseAndRepeatedWhitespace()
    {
        var first = TtsuBookImporter.NormalizeTitle("  Test   Book ");
        var second = TtsuBookImporter.NormalizeTitle("test book");

        Assert.Equal(first, second);
    }

    private static TtsuBookContainer Book(params TtsuReaderDTO[] entries)
    {
        return new TtsuBookContainer
        {
            Title = "Test Book",
            Entries = [.. entries]
        };
    }

    private static TtsuReaderDTO Entry(string date, int characters, long modified)
    {
        return new TtsuReaderDTO
        {
            Title = "Test Book",
            DateKey = date,
            CharactersRead = characters,
            ReadingTime = 60,
            LastStatisticModified = modified
        };
    }
}
