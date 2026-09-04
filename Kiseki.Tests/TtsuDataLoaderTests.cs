using System.Text;
using Kiseki.Core.Services;

namespace Kiseki.Tests;

public sealed class TtsuDataLoaderTests
{
    private readonly TtsuDataLoader _loader = new();

    [Fact]
    public async Task ParseStatisticsAsync_UsesJsonTitleAndReadsEntries()
    {
        await using var stream = File.OpenRead(GetFixturePath());

        var book = await _loader.ParseStatisticsAsync(stream, "Folder title");

        Assert.Equal("Test Book", book.Title);
        Assert.Equal(2, book.Entries.Count);
        Assert.Equal(18_610, book.Entries.Sum(entry => entry.CharactersRead));
    }

    [Fact]
    public async Task ParseStatisticsAsync_UsesFolderTitleForAnEmptyEntryList()
    {
        await using var stream = JsonStream("[]");

        var book = await _loader.ParseStatisticsAsync(stream, "Empty Book");

        Assert.Equal("Empty Book", book.Title);
        Assert.Empty(book.Entries);
    }

    [Fact]
    public async Task ParseStatisticsAsync_RejectsMalformedJson()
    {
        await using var stream = JsonStream("not json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _loader.ParseStatisticsAsync(stream, "Book"));

        Assert.Contains("valid TTSU statistics JSON", exception.Message);
    }

    [Fact]
    public async Task ParseStatisticsAsync_RejectsInvalidDates()
    {
        const string json = """
            [{
              "title": "Test Book",
              "dateKey": "08/05/2026",
              "charactersRead": 100,
              "readingTime": 60,
              "lastStatisticModified": 1
            }]
            """;
        await using var stream = JsonStream(json);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => _loader.ParseStatisticsAsync(stream));

        Assert.Contains("yyyy-MM-dd", exception.Message);
    }

    [Fact]
    public async Task LoadDirectoryAsync_LoadsBookFoldersAndSkipsFoldersWithoutStatistics()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "Kiseki.Tests", Guid.NewGuid().ToString("N"));
        var firstBookPath = Directory.CreateDirectory(Path.Combine(rootPath, "First Book")).FullName;
        var secondBookPath = Directory.CreateDirectory(Path.Combine(rootPath, "Second Book")).FullName;
        Directory.CreateDirectory(Path.Combine(rootPath, "No Statistics"));

        try
        {
            var fixture = await File.ReadAllTextAsync(GetFixturePath());
            await File.WriteAllTextAsync(Path.Combine(firstBookPath, "statistics.json"), fixture);
            await File.WriteAllTextAsync(
                Path.Combine(secondBookPath, "statistics.json"),
                fixture.Replace("Test Book", "Second Book", StringComparison.Ordinal));

            var books = await _loader.LoadDirectoryAsync(rootPath);

            Assert.Equal(2, books.Count);
            Assert.Equal(["Test Book", "Second Book"], books.Select(book => book.Title));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadDirectoryAsync_ReturnsEmptyWhenNoBookFolderHasStatistics()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "Kiseki.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rootPath, "Empty Book"));

        try
        {
            var books = await _loader.LoadDirectoryAsync(rootPath);

            Assert.Empty(books);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadDirectoryAsync_UsesTheNewestStatisticsFileForABook()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "Kiseki.Tests", Guid.NewGuid().ToString("N"));
        var bookPath = Directory.CreateDirectory(Path.Combine(rootPath, "Book")).FullName;

        try
        {
            var fixture = await File.ReadAllTextAsync(GetFixturePath());
            var olderPath = Path.Combine(bookPath, "statistics-old.json");
            var newerPath = Path.Combine(bookPath, "statistics-new.json");
            await File.WriteAllTextAsync(olderPath, fixture.Replace("Test Book", "Older", StringComparison.Ordinal));
            await File.WriteAllTextAsync(newerPath, fixture.Replace("Test Book", "Newer", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

            var books = await _loader.LoadDirectoryAsync(rootPath);

            Assert.Equal("Newer", Assert.Single(books).Title);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static string GetFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "ttsu-statistics.json");
    }

    private static MemoryStream JsonStream(string json)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }
}
