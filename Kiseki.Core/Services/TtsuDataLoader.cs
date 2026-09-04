using System.Text.Json;
using Kiseki.Core.DTOs;

namespace Kiseki.Core.Services;

public sealed class TtsuDataLoader
{
    public const string StatisticsFilePrefix = "statistics";

    public async Task<IReadOnlyList<TtsuBookContainer>> LoadDirectoryAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A TTSU data directory is required.", nameof(rootPath));
        }

        var fullRootPath = Path.GetFullPath(rootPath.Trim());
        if (!Directory.Exists(fullRootPath))
        {
            throw new DirectoryNotFoundException(
                $"The TTSU data directory '{fullRootPath}' does not exist.");
        }

        var books = new List<TtsuBookContainer>();
        var bookDirectories = Directory
            .EnumerateDirectories(fullRootPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var bookDirectory in bookDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var statisticsFile = Directory
                .EnumerateFiles(bookDirectory)
                .Where(IsStatisticsFileName)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (statisticsFile is null)
            {
                continue;
            }

            await using var stream = new FileStream(
                statisticsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            try
            {
                books.Add(await ParseStatisticsAsync(
                    stream,
                    Path.GetFileName(bookDirectory),
                    cancellationToken));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Could not load TTSU statistics from '{statisticsFile}'. {exception.Message}",
                    exception);
            }
        }

        return books;
    }

    public async Task<TtsuBookContainer> ParseStatisticsAsync(
        Stream jsonStream,
        string? fallbackTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);

        if (!jsonStream.CanRead)
        {
            throw new ArgumentException("The TTSU statistics stream must be readable.", nameof(jsonStream));
        }

        List<TtsuReaderDTO>? entries;
        try
        {
            entries = await JsonSerializer.DeserializeAsync<List<TtsuReaderDTO>>(
                jsonStream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The file does not contain valid TTSU statistics JSON.", exception);
        }

        if (entries is null)
        {
            throw new InvalidDataException("The TTSU statistics file did not contain an entry list.");
        }

        for (var index = 0; index < entries.Count; index++)
        {
            ValidateEntry(entries[index], index);
        }

        var title = entries
            .Select(entry => entry.Title?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? fallbackTitle?.Trim();

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidDataException(
                "The TTSU statistics file does not identify its book and no folder title was available.");
        }

        return new TtsuBookContainer
        {
            Title = title,
            Entries = entries
        };
    }

    public static bool IsStatisticsFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        var fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        return fileName.StartsWith(StatisticsFilePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateEntry(TtsuReaderDTO entry, int index)
    {
        if (!TtsuSessionMapper.TryParseDate(entry.DateKey, out _))
        {
            throw new InvalidDataException(
                $"Entry {index + 1} has an invalid dateKey '{entry.DateKey}'. Expected yyyy-MM-dd.");
        }

        if (entry.CharactersRead < 0)
        {
            throw new InvalidDataException($"Entry {index + 1} has a negative character count.");
        }

        if (entry.ReadingTime < 0 || double.IsNaN(entry.ReadingTime) || double.IsInfinity(entry.ReadingTime))
        {
            throw new InvalidDataException($"Entry {index + 1} has an invalid reading time.");
        }
    }
}
