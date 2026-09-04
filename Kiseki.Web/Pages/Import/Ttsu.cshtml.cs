using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Web.Pages.Import;

[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueCountLimit = 10_000)]
public sealed class TtsuModel(
    TtsuDataLoader dataLoader,
    ITtsuImportBatchStore batchStore,
    ImmersionDbContext dbContext) : PageModel
{
    private const int MaxStatisticsFiles = 250;
    private const long MaxStatisticsFileBytes = 8 * 1024 * 1024;
    private const long MaxRequestBytes = 64 * 1024 * 1024;

    [BindProperty]
    public List<IFormFile> FolderFiles { get; set; } = [];

    [BindProperty]
    public Guid BatchId { get; set; }

    [BindProperty]
    public List<TtsuBookSelectionInput> Selections { get; set; } = [];

    public IReadOnlyList<TtsuBookPreviewViewModel> Books { get; private set; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasPreview => BatchId != Guid.Empty && Books.Count > 0;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        if (FolderFiles.Count == 0)
        {
            ModelState.AddModelError(nameof(FolderFiles), "Choose your TTSU data folder first.");
            return Page();
        }

        var statisticsFiles = FolderFiles
            .Where(file => TtsuDataLoader.IsStatisticsFileName(file.FileName))
            .ToList();

        if (statisticsFiles.Count == 0)
        {
            ModelState.AddModelError(
                nameof(FolderFiles),
                "No TTSU statistics files were found in the selected folder.");
            return Page();
        }

        if (statisticsFiles.Count > MaxStatisticsFiles)
        {
            ModelState.AddModelError(
                nameof(FolderFiles),
                $"The folder contains more than the supported limit of {MaxStatisticsFiles} statistics files.");
            return Page();
        }

        var parsedBooks = new List<ParsedBook>();
        foreach (var file in statisticsFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = DisplayFileName(file.FileName);

            if (file.Length == 0)
            {
                Warnings.Add($"{displayName} was empty and was skipped.");
                continue;
            }

            if (file.Length > MaxStatisticsFileBytes)
            {
                Warnings.Add($"{displayName} was larger than 8 MB and was skipped.");
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var book = await dataLoader.ParseStatisticsAsync(
                    stream,
                    FolderTitle(file.FileName),
                    cancellationToken);
                var latestModified = book.Entries.Count == 0
                    ? 0
                    : book.Entries.Max(entry => entry.LastStatisticModified);

                parsedBooks.Add(new ParsedBook(book, displayName, latestModified));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                Warnings.Add($"{displayName} was skipped: {exception.Message}");
            }
        }

        var books = parsedBooks
            .GroupBy(item => TtsuBookImporter.NormalizeTitle(item.Book.Title))
            .Select(group =>
            {
                var selected = group
                    .OrderByDescending(item => item.LatestModified)
                    .ThenBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
                    .First();

                if (group.Count() > 1)
                {
                    Warnings.Add(
                        $"Multiple statistics files described '{selected.Book.Title}'. " +
                        $"The newest file, {selected.SourceName}, was used.");
                }

                return selected.Book;
            })
            .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (books.Count == 0)
        {
            ModelState.AddModelError(
                nameof(FolderFiles),
                "None of the detected statistics files could be read.");
            return Page();
        }

        var batch = batchStore.Store(books);
        await PopulatePreviewAsync(batch, preserveSelections: false, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (BatchId == Guid.Empty || !batchStore.TryGet(BatchId, out var batch))
        {
            ModelState.AddModelError(
                string.Empty,
                "This import preview has expired. Choose the TTSU folder again.");
            BatchId = Guid.Empty;
            return Page();
        }

        if (!ModelState.IsValid)
        {
            await PopulatePreviewAsync(batch, preserveSelections: true, cancellationToken);
            return Page();
        }

        var selectedInputs = Selections
            .Where(selection => selection.Selected)
            .GroupBy(selection => selection.BookKey)
            .Select(group => group.First())
            .ToList();

        if (selectedInputs.Count == 0)
        {
            ModelState.AddModelError(nameof(Selections), "Select at least one book to import.");
            await PopulatePreviewAsync(batch, preserveSelections: true, cancellationToken);
            return Page();
        }

        var batchBooks = batch.Books.ToDictionary(item => item.Key);
        if (selectedInputs.Any(selection => !batchBooks.ContainsKey(selection.BookKey)))
        {
            ModelState.AddModelError(string.Empty, "The selected import data is no longer valid.");
            await PopulatePreviewAsync(batch, preserveSelections: true, cancellationToken);
            return Page();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var existingWorks = await dbContext.MediaWorks
            .Where(work => work.MediaType == MediaType.Book)
            .Include(work => work.Logs)
            .ToListAsync(cancellationToken);
        var existingByTitle = existingWorks
            .GroupBy(work => TtsuBookImporter.NormalizeTitle(work.Title))
            .ToDictionary(group => group.Key, group => group.First());

        var importedBooks = 0;
        var addedSessions = 0;
        var updatedSessions = 0;

        foreach (var selection in selectedInputs)
        {
            var book = batchBooks[selection.BookKey].Book;
            var normalizedTitle = TtsuBookImporter.NormalizeTitle(book.Title);

            if (selection.Mode == TtsuImportMode.Merge &&
                existingByTitle.TryGetValue(normalizedTitle, out var existingWork))
            {
                var result = TtsuBookImporter.MergeInto(existingWork, book);
                dbContext.ImmersionLogs.AddRange(result.AddedLogs);
                addedSessions += result.AddedSessions;
                updatedSessions += result.UpdatedSessions;
            }
            else
            {
                var newWork = TtsuBookImporter.CreateMediaWork(book);
                dbContext.MediaWorks.Add(newWork);
                addedSessions += newWork.Logs.Count;

                if (selection.Mode == TtsuImportMode.Merge)
                {
                    existingByTitle[normalizedTitle] = newWork;
                }
            }

            importedBooks++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        batchStore.Remove(batch.Id);

        TempData["LibraryNotice"] = BuildSuccessMessage(
            importedBooks,
            addedSessions,
            updatedSessions);

        return RedirectToPage("/Library/Index");
    }

    public IActionResult OnPostCancel()
    {
        if (BatchId != Guid.Empty)
        {
            batchStore.Remove(BatchId);
        }

        return RedirectToPage();
    }

    private async Task PopulatePreviewAsync(
        TtsuImportBatch batch,
        bool preserveSelections,
        CancellationToken cancellationToken)
    {
        BatchId = batch.Id;

        var existingWorks = await dbContext.MediaWorks
            .AsNoTracking()
            .Where(work => work.MediaType == MediaType.Book)
            .Select(work => new { work.Id, work.Title })
            .ToListAsync(cancellationToken);
        var existingByTitle = existingWorks
            .GroupBy(work => TtsuBookImporter.NormalizeTitle(work.Title))
            .ToDictionary(group => group.Key, group => group.First().Id);

        Books = batch.Books
            .Select(item =>
            {
                var normalizedTitle = TtsuBookImporter.NormalizeTitle(item.Book.Title);
                Guid? existingWorkId = existingByTitle.TryGetValue(normalizedTitle, out var workId)
                    ? workId
                    : null;

                return TtsuBookPreviewViewModel.Create(item.Key, item.Book, existingWorkId);
            })
            .ToList();

        var postedSelections = preserveSelections
            ? Selections
                .GroupBy(selection => selection.BookKey)
                .ToDictionary(group => group.Key, group => group.First())
            : [];

        Selections = Books
            .Select(book => postedSelections.GetValueOrDefault(book.BookKey) ?? new TtsuBookSelectionInput
            {
                BookKey = book.BookKey,
                Selected = true,
                Mode = TtsuImportMode.Merge
            })
            .ToList();
    }

    private static string? FolderTitle(string uploadedFileName)
    {
        var parts = uploadedFileName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 1 ? parts[^2] : null;
    }

    private static string DisplayFileName(string uploadedFileName)
    {
        var normalized = uploadedFileName.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static string BuildSuccessMessage(int books, int addedSessions, int updatedSessions)
    {
        var bookLabel = books == 1 ? "book" : "books";
        var sessionLabel = addedSessions == 1 ? "session" : "sessions";
        var message = $"Imported {books} {bookLabel} with {addedSessions} new {sessionLabel}.";

        if (updatedSessions > 0)
        {
            var updatedLabel = updatedSessions == 1 ? "session was" : "sessions were";
            message += $" {updatedSessions} existing {updatedLabel} updated.";
        }

        return message;
    }

    private sealed record ParsedBook(
        TtsuBookContainer Book,
        string SourceName,
        long LatestModified);
}
