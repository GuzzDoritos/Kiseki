using Kiseki.Core;
using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;
using Kiseki.Core.Services;
using Kiseki.Web.Models;
using Kiseki.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kiseki.Web.Pages.Import;

[RequestSizeLimit(MaxRequestBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueCountLimit = 100_000)]
public sealed class TtsuModel(TtsuDataLoader dataLoader, ITtsuImportBatchStore batchStore, ImmersionDbContext dbContext) : PageModel
{
    private const long MaxRequestBytes = 64 * 1024 * 1024;
    private readonly TtsuImportService _imports = new(dbContext);
    [BindProperty] public List<IFormFile> FolderFiles { get; set; } = [];
    [BindProperty] public Guid BatchId { get; set; }
    [BindProperty] public List<TtsuBookSelectionInput> Selections { get; set; } = [];
    public IReadOnlyList<TtsuBookPreviewViewModel> Books { get; private set; } = [];
    public IReadOnlyList<TtsuTarget> Targets { get; private set; } = [];
    public IReadOnlyList<TtsuOrphanViewModel> Orphans { get; private set; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasPreview => BatchId != Guid.Empty && Books.Count > 0;
    public void OnGet() { }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken cancellationToken)
    {
        var files = FolderFiles.Where(file => TtsuDataLoader.IsStatisticsFileName(file.FileName) ||
            TtsuDataLoader.IsProgressFileName(file.FileName)).ToList();
        var statisticsFileCount = files.Count(file => TtsuDataLoader.IsStatisticsFileName(file.FileName));
        if (statisticsFileCount == 0 || files.Count > 250)
        {
            ModelState.AddModelError(nameof(FolderFiles), "Choose a TTSU folder with statistics files and no more than 250 statistics/progress files.");
            return Page();
        }
        var parsed = new List<TtsuBookContainer>();
        var progressByFolder = new Dictionary<string, List<TtsuProgressDTO>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.FileName.Replace('\\', '/');
            if (file.Length is 0 or > 8 * 1024 * 1024)
            {
                Warnings.Add($"{path} was empty or larger than 8 MB and was skipped.");
                continue;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var folder = parts.Length > 1 ? parts[^2] : null;
                // Ignore the chosen root directory, which changes between exports/machines.
                var folderHint = parts.Length > 2 ? string.Join('/', parts.Skip(1).SkipLast(1)) : folder;
                if (TtsuDataLoader.IsProgressFileName(path))
                {
                    var progress = await dataLoader.ParseProgressAsync(stream, path, cancellationToken);
                    progressByFolder.TryAdd(folderHint ?? string.Empty, []);
                    progressByFolder[folderHint ?? string.Empty].Add(progress);
                }
                else
                {
                    var book = await dataLoader.ParseStatisticsAsync(stream, folder, cancellationToken);
                    book.FolderHint = folderHint;
                    parsed.Add(book);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                Warnings.Add($"{path} was skipped: {exception.Message}");
            }
        }
        if (parsed.Count == 0)
        {
            ModelState.AddModelError(nameof(FolderFiles), "None of the detected statistics files could be read.");
            return Page();
        }
        var combined = TtsuStatisticsNormalizer.CombineFiles(parsed).ToList();
        foreach (var (folderHint, progressEntries) in progressByFolder)
        {
            var matches = combined.Where(book => string.Equals(book.FolderHint ?? string.Empty,
                folderHint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                matches[0].ProgressEntries.AddRange(progressEntries);
            }
            else
            {
                Warnings.Add(matches.Count == 0
                    ? $"Progress in '{folderHint}' was skipped because no statistics file identified its book."
                    : $"Progress in '{folderHint}' was skipped because multiple book titles made it ambiguous.");
            }
        }
        var batch = batchStore.Store(combined);
        await PopulateAsync(batch, false, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken cancellationToken)
    {
        if (!TryBatch(out var batch)) return Page();
        await PopulateAsync(batch, true, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        var receipt = await _imports.GetReceiptAsync(BatchId, cancellationToken);
        if (receipt is not null) return Success(receipt);
        if (!TryBatch(out var batch)) return Page();
        if (!ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "One or more import choices were invalid. Review the selections.");
            await PopulateAsync(batch, true, cancellationToken);
            return Page();
        }
        try
        {
            var selected = Selections.Where(x => x.Selected).ToList();
            if (selected.Select(x => x.BookKey).Distinct().Count() != selected.Count)
                throw new TtsuImportReviewRequiredException("A book was selected more than once.");
            var requests = new List<TtsuImportRequest>();
            foreach (var input in selected)
            {
                var book = batch.Books.SingleOrDefault(x => x.Key == input.BookKey);
                if (book is null || !Enum.IsDefined(input.Mode) || !batch.Reviews.TryGetValue(input.ReviewToken, out var review) || review.BookKey != input.BookKey)
                    throw new TtsuImportReviewRequiredException("The selection is invalid. Review the import again.");
                if (input.Mode == TtsuImportMode.Merge && input.TargetId is null)
                    throw new TtsuImportReviewRequiredException("Choose an existing book or explicitly choose Create a new copy.");
                requests.Add(new(book.Book, input.Mode == TtsuImportMode.Create ? null : input.TargetId,
                    Resolutions(input), review.Fingerprint, input.OrphanLogIds, input.ProgressChoice));
            }
            receipt = await _imports.ApplyAsync(batch.Id, requests, cancellationToken);
            batchStore.Remove(batch.Id);
            return Success(receipt);
        }
        catch (TtsuImportReviewRequiredException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await PopulateAsync(batch, true, cancellationToken);
            return Page();
        }
    }

    public IActionResult OnPostCancel()
    {
        batchStore.Remove(BatchId);
        return RedirectToPage();
    }

    private bool TryBatch(out TtsuImportBatch batch)
    {
        if (BatchId != Guid.Empty && batchStore.TryGet(BatchId, out batch)) return true;
        batch = null!;
        ModelState.AddModelError(string.Empty, "This import preview has expired. Choose the TTSU folder again.");
        BatchId = Guid.Empty;
        return false;
    }

    private async Task PopulateAsync(TtsuImportBatch batch, bool preserve, CancellationToken cancellationToken)
    {
        BatchId = batch.Id;
        Targets = await _imports.GetTargetsAsync(cancellationToken);
        Orphans = (await _imports.GetOrphansAsync(cancellationToken)).Select(x => new TtsuOrphanViewModel(x.Id, x.Date, x.CharactersRead, x.TimeSpentMinutes)).ToList();
        var posted = preserve ? Selections.GroupBy(x => x.BookKey).ToDictionary(x => x.Key, x => x.First()) : [];
        var books = new List<TtsuBookPreviewViewModel>();
        Selections = [];
        foreach (var item in batch.Books)
        {
            var match = await _imports.MatchAsync(item.Book, cancellationToken);
            var input = posted.GetValueOrDefault(item.Key) ?? new TtsuBookSelectionInput
            {
                BookKey = item.Key,
                Selected = true,
                TargetId = match.WorkId,
                Mode = match.WorkId is null && !match.IsAmbiguous ? TtsuImportMode.Create : TtsuImportMode.Merge
            };
            var plan = await _imports.PreviewAsync(item.Book, input.Mode == TtsuImportMode.Create ? null : input.TargetId,
                Resolutions(input), input.OrphanLogIds, cancellationToken, input.ProgressChoice);
            if (!Enum.IsDefined(input.Mode) || input.Mode == TtsuImportMode.Merge && input.TargetId is null)
                plan = plan with { Error = "Choose a target book or create a new copy." };
            var choices = Resolutions(input);
            input.Days = plan.Days.Where(x => x.ReviewReason is not null).Select(x => new TtsuDayResolutionInput
            { Date = x.Date, Choice = choices.GetValueOrDefault(x.Date) ?? string.Empty }).ToList();
            input.ReviewToken = Guid.NewGuid();
            batch.Reviews[input.ReviewToken] = new(item.Key, plan.Fingerprint);
            books.Add(new(item.Key, item.Book.Title, item.Book.FolderHint, match.Reason, plan));
            Selections.Add(input);
        }
        Books = books;
        // Render newly generated review tokens and normalized choices, not the posted values.
        foreach (var key in ModelState.Keys.Where(x => x.StartsWith("Selections[", StringComparison.Ordinal)).ToList())
            ModelState.Remove(key);
    }

    private static Dictionary<DateOnly, string> Resolutions(TtsuBookSelectionInput input) =>
        input.Days.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.First().Choice);

    private IActionResult Success(TtsuImportReceipt receipt)
    {
        TempData["LibraryNotice"] = receipt.AddedDays == 0 && receipt.UpdatedDays == 0 && receipt.ProgressUpdates == 0
            ? $"Statistics and progress are already up to date. {receipt.StaleDays} older days skipped."
            : $"Imported {receipt.Books} books: {receipt.AddedDays} new days, {receipt.UpdatedDays} updated days, {receipt.UnchangedDays} unchanged days, {receipt.StaleDays} older days skipped, {receipt.ProgressUpdates} progress updates.";
        return RedirectToPage("/Library/Index");
    }
}
