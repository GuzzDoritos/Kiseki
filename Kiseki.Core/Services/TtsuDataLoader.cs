using System.Text.Json;
using Kiseki.Core.DTOs;

namespace Kiseki.Core.Services;

public sealed class TtsuDataLoader
{
    public const string StatisticsFilePrefix = "statistics";
    public const string ProgressFilePrefix = "progress_";

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

            var sourceFiles = Directory
                .EnumerateFiles(bookDirectory)
                .Where(path => IsStatisticsFileName(path) || IsProgressFileName(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            var folderBooks = new List<TtsuBookContainer>();
            var progressEntries = new List<TtsuProgressDTO>();
            foreach (var sourceFile in sourceFiles)
            {
                await using var stream = new FileStream(
                    sourceFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    useAsync: true);

                try
                {
                    if (IsProgressFileName(sourceFile))
                    {
                        progressEntries.Add(await ParseProgressAsync(stream, sourceFile, cancellationToken));
                    }
                    else
                    {
                        var book = await ParseStatisticsAsync(
                            stream,
                            Path.GetFileName(bookDirectory),
                            cancellationToken);
                        book.FolderHint = Path.GetFileName(bookDirectory);
                        folderBooks.Add(book);
                    }
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException(
                        $"Could not load TTSU data from '{sourceFile}'. {exception.Message}",
                        exception);
                }
            }

            var combinedFolderBooks = TtsuStatisticsNormalizer.CombineFiles(folderBooks);
            if (progressEntries.Count > 0 && combinedFolderBooks.Count > 1)
            {
                throw new InvalidDataException(
                    $"The TTSU folder '{bookDirectory}' contains multiple book titles, so its progress file is ambiguous.");
            }
            if (combinedFolderBooks.Count == 1)
            {
                combinedFolderBooks[0].ProgressEntries.AddRange(progressEntries);
            }
            books.AddRange(combinedFolderBooks);
        }
        return TtsuStatisticsNormalizer.CombineFiles(books);
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

        var book = new TtsuBookContainer
        {
            Title = title,
            Entries = entries
        };
        TtsuStatisticsNormalizer.Normalize(book);
        return book;
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

    public static bool IsProgressFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Replace('\\', '/');
        var fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        return fileName.StartsWith(ProgressFilePrefix, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TtsuProgressDTO> ParseProgressAsync(
        Stream jsonStream,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);
        if (!jsonStream.CanRead)
        {
            throw new ArgumentException("The TTSU progress stream must be readable.", nameof(jsonStream));
        }

        TtsuProgressDTO? progress;
        try
        {
            progress = await JsonSerializer.DeserializeAsync<TtsuProgressDTO>(jsonStream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The file does not contain valid TTSU progress JSON.", exception);
        }

        if (progress is null)
        {
            throw new InvalidDataException("The TTSU progress file was empty.");
        }

        ApplyProgressFileMetadata(progress, fileName);
        _ = TtsuProgressNormalizer.Normalize(progress);
        return progress;
    }

    private static void ApplyProgressFileMetadata(TtsuProgressDTO progress, string? path)
    {
        if (!IsProgressFileName(path))
        {
            return;
        }

        var fileName = Path.GetFileNameWithoutExtension(path!);
        var parts = fileName.Split('_');
        if (parts.Length >= 5)
        {
            progress.ExporterVersion = int.TryParse(parts[1], out var exporterVersion) ? exporterVersion : null;
            progress.DatabaseVersion = int.TryParse(parts[2], out var databaseVersion) ? databaseVersion : null;
        }
    }

    private static void ValidateEntry(TtsuReaderDTO entry, int index)
    {
        if (entry is null)
            throw new InvalidDataException($"Entry {index + 1} is null.");
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
