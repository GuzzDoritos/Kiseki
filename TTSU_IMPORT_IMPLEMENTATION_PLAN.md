# TTSU Web Import Implementation Plan

## Implementation status

The initial vertical slice described below is implemented.

- Core parses either uploaded streams or a caller-supplied desktop directory.
- Console asks for the directory lazily and supports `KISEKI_TTSU_DATA_PATH` as a default.
- Web supports folder selection, preview, warnings, per-book selection, and confirmation.
- Parsed previews live in a 30-minute in-memory batch and are removed after import or cancellation.
- Matching is based on a case-insensitive, whitespace-normalized book title.
- The default behavior merges into a matching work; the user can explicitly create another copy.
- Within a matching work, a TTSU entry is identified by reading date. Reimport updates that date and adds new dates, making repeated imports predictable without a schema migration.
- Automatic Jiten matching, permanent import batches, and background progress remain intentionally deferred.

## Goal

Allow a user to select a local TTSU data folder from the Kiseki Web import page, preview the books and reading statistics found in it, choose what to import, and confirm before anything is saved to the library.

The implementation should preserve the existing Razor Pages architecture and keep filesystem- and ASP.NET-specific concerns out of `Kiseki.Core`.

## Intended flow

```text
Select TTSU folder
    -> browser uploads the selected files
    -> Web identifies TTSU statistics files
    -> Core parses each statistics JSON stream
    -> Web displays a read-only preview
    -> user selects books and confirms
    -> selected books and immersion logs are saved
    -> user is redirected to the library
```

## Important constraint

A browser cannot provide the server with a usable local folder path. The Web project therefore cannot call a Core method that expects something such as `C:\ttu-reader-data` after a user picks a folder.

The browser must upload the selected files. The Web project receives them as `IFormFile` objects and passes their streams to Core. The Console project can continue reading directly from a directory.

## Stage 1: Define and test the TTSU input contract

### Objective

Document what a valid TTSU export looks like before changing the loader.

### Tasks

- Confirm the expected directory structure.
- Confirm the naming pattern for statistics files, currently `statistics*`.
- Keep a small sanitized statistics JSON fixture in the test project.
- Decide which value is authoritative for a book title:
  - the title contained in the JSON;
  - the containing directory name;
  - JSON title first, directory name as fallback (recommended).
- Decide how to handle:
  - folders without a statistics file;
  - empty statistics files;
  - malformed JSON;
  - entries with invalid dates;
  - multiple statistics files for one book.

### Acceptance criteria

- The expected file structure and failure behavior are unambiguous.
- Tests have realistic input data without relying on the developer's TTSU installation.

## Stage 2: Refactor the Core loader

### Objective

Separate JSON parsing from local directory traversal so both Console and Web can reuse the same parser.

### Primary file

- `Kiseki.Core/Services/TtsuDataLoader.cs`

### Suggested API shape

```csharp
Task<IReadOnlyList<TtsuBookContainer>> LoadDirectoryAsync(
    string rootPath,
    CancellationToken cancellationToken = default);

Task<TtsuBookContainer> ParseStatisticsAsync(
    Stream jsonStream,
    string? fallbackTitle = null,
    CancellationToken cancellationToken = default);
```

The exact names can change, but there should be two distinct responsibilities:

- `LoadDirectoryAsync` discovers files for desktop/Console callers.
- `ParseStatisticsAsync` understands one uploaded statistics file and has no dependency on ASP.NET.

### Tasks

- Remove the hardcoded `C:\ttu-reader-data` field.
- Accept the root path from the caller.
- Deserialize directly from a stream with asynchronous APIs.
- Use `Path.GetFileName` instead of manually removing parent paths.
- Handle missing statistics files without dereferencing `null`.
- Validate deserialization results and return useful errors.
- Initialize `TtsuBookContainer.Title` and `Entries` to safe non-null values.
- Keep `IFormFile` out of Core.

### Tests

- Parses a valid statistics stream.
- Produces the expected title and entries.
- Handles an empty list.
- Rejects malformed JSON with a useful error.
- Handles a directory containing no statistics files.
- Handles more than one book directory.

### Acceptance criteria

- Core can parse TTSU data without knowing where the stream came from.
- No hardcoded machine-specific path remains.
- Existing date conversion tests continue to pass.

## Stage 3: Adapt the Console caller

### Objective

Keep the existing Console import working after the loader refactor.

### Primary file

- `Kiseki.Console/Program.cs`

### Tasks

- Obtain the TTSU root from configuration, an environment variable, a command-line option, or an interactive prompt.
- Pass that path to `LoadDirectoryAsync`.
- Display a friendly message when the directory does not exist or contains no books.
- Avoid loading the folder at startup if TTSU import is not being used, if practical.

### Acceptance criteria

- Console import still finds and imports books.
- Starting the Console app does not crash when no TTSU folder is configured.

## Stage 4: Add folder upload to the Razor Page

### Objective

Let the user select a TTSU folder and submit it to a preview handler.

### Primary files

- `Kiseki.Web/Pages/Import/Ttsu.cshtml`
- `Kiseki.Web/Pages/Import/Ttsu.cshtml.cs`

### Suggested form shape

```razor
<form method="post"
      enctype="multipart/form-data"
      asp-page-handler="Preview">
    <input asp-for="FolderFiles"
           type="file"
           webkitdirectory
           multiple />

    <button class="btn btn-primary" type="submit">
        Scan TTSU folder
    </button>
</form>
```

### PageModel responsibilities

- Bind the uploaded files as a collection of `IFormFile`.
- Select files matching the supported statistics naming pattern.
- Open each file as a read-only stream.
- Pass each stream to the Core parser.
- Collect successful books and user-facing warnings.

### Validation

- Require at least one uploaded file.
- Require at least one recognized statistics file.
- Limit the number and size of accepted files.
- Do not construct local server paths from uploaded filenames.
- Do not save uploads permanently during preview.
- Report invalid books without losing valid books from the same batch.

### Note on folder uploads

`webkitdirectory` is the simplest choice for the initial implementation. A later JavaScript enhancement can filter the selected folder client-side so unrelated or large files are not uploaded.

### Acceptance criteria

- Clicking the button opens a folder chooser.
- Selecting a valid TTSU root submits its statistics data.
- Invalid or empty selections produce a helpful validation message.
- Nothing is written to the database yet.

## Stage 5: Build the preview model and UI

### Objective

Show users what was detected before asking them to import anything.

### Suggested new Web model

- `Kiseki.Web/Models/TtsuBookPreviewViewModel.cs`

### Suggested fields

```text
Book key within the import batch
Title
Number of reading entries
Total characters read
Total reading time
First reading date
Last reading date
Warnings, if any
Selected for import
```

### Suggested summary row

```text
Select | Title | Sessions | Characters | Reading time | Date range
```

Individual reading entries can be shown in an expandable details area with:

```text
Date | Characters | Reading time
```

### Calculation rules

- Sessions: `Entries.Count`.
- Characters: sum of `CharactersRead`.
- Reading time: sum of `ReadingTime`, converted from seconds for display.
- Date range: earliest and latest valid `DateKey` values.
- Formatting belongs in the Web view model or Razor view, not in the raw DTO.

### Acceptance criteria

- Every valid detected book appears once.
- Totals agree with the uploaded entries.
- The user can select or deselect books.
- The preview is clearly read-only and does not imply that import has already happened.

## Stage 6: Preserve the preview between requests

### Objective

Retain parsed data while the user reviews it and submits a separate confirmation request.

File inputs are not repopulated after the preview response, so the confirmation POST cannot rely on the browser uploading the folder a second time.

### Recommended initial approach

Use `IMemoryCache` as temporary import-batch storage:

1. Generate a random batch `Guid` after parsing.
2. Store the parsed `TtsuBookContainer` collection under that ID.
3. Set a short sliding or absolute expiration, such as 15–30 minutes.
4. Include only the batch ID and selected book keys in the confirmation form.
5. Remove the batch after successful import or cancellation.

### Avoid

- Putting all parsed entries in cookie-backed `TempData`.
- Trusting totals posted back from hidden form fields.
- Keeping uploaded streams open between requests.

### Acceptance criteria

- Confirmation uses server-held parsed data rather than client-supplied totals.
- Expired or missing batches result in a friendly request to select the folder again.
- Two simultaneous imports cannot overwrite one another.

## Stage 7: Import confirmed books

### Objective

Convert selected previews into real library records only after explicit confirmation.

### Tasks

- Retrieve the temporary batch using its ID.
- Resolve the selected books against that batch.
- Create one `MediaWork` per selected book.
- Set `MediaType.Book`.
- Convert every TTSU entry through the existing `TtsuSessionMapper.ToImmersionLog` method.
- Add the logs to the corresponding `MediaWork`.
- Save all selected books in one database transaction.
- Remove the temporary batch after a successful save.
- Redirect to the Library using Post/Redirect/Get.
- Show a small success summary, such as books and sessions imported.

### Shared behavior

If both Console and Web create `MediaWork` objects from `TtsuBookContainer`, extract that conversion into a small Core service or factory. Database saving can remain with the caller.

Do not use `MediaAggregator.ImportTtsuSessionsAsync` for previewing in its current form. It creates a `MediaWork`, performs a Jiten request, and stores results in an in-memory list, which are separate concerns from parsing and previewing.

### Acceptance criteria

- Previewing alone never changes the library.
- Only selected books are imported.
- Imported dates, character counts, and reading times match the preview.
- A failed multi-book import does not leave a partially saved batch.

## Stage 8: Define duplicate-import behavior

### Objective

Prevent repeated imports from silently duplicating books or immersion logs.

### Decisions required

- How is an existing `MediaWork` matched: exact title, normalized title, explicit user choice, or a stored TTSU identifier?
- When the work exists, should Kiseki:
  - merge new sessions;
  - replace existing TTSU sessions;
  - create another work;
  - ask the user?
- What makes a TTSU entry unique?

### Recommended direction

- Let the preview flag likely existing works and ask whether to merge or create separately.
- Introduce a stable external source key for imported logs if TTSU provides enough data to construct one reliably.
- Treat source identity as a domain/data-integrity concern, not a UI-only field.

This stage may require a database migration if source identifiers or import metadata are added to the domain model.

### Acceptance criteria

- Re-importing the same export has predictable behavior.
- The user sees potential duplicates before confirmation.
- Duplicate prevention is enforced server-side, not only by the page.

## Stage 9: Polish and resilience

### Optional improvements

- Client-side filtering so only `statistics*` files are uploaded.
- Import progress for unusually large folders.
- Cancellation tokens throughout parsing and persistence.
- A warning/details panel for skipped files.
- Better accessibility for the folder picker and preview table.
- Persisted import batches if imports must survive application restarts.
- Optional Jiten matching after TTSU books have been previewed.

## Testing checklist

### Core

- Stream parsing and title fallback.
- Date conversion.
- Character and time values.
- Missing, empty, and malformed data.
- Directory discovery without a configured folder.

### Web

- GET displays the folder picker.
- Preview POST rejects an empty selection.
- Preview POST accepts multiple valid books.
- Preview displays accurate totals.
- Confirm imports only selected books.
- Missing or expired batch is handled.
- Invalid uploads do not create database records.

### Persistence

- All selected books and logs save atomically.
- Re-import follows the chosen duplicate policy.
- Library totals reflect newly imported logs.

### Final verification after every stage

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.Tests/Kiseki.Tests.csproj --no-build
```

## Scope boundaries

The initial vertical slice should not include:

- automatic Jiten matching;
- series or franchise assignment;
- background jobs;
- permanent upload storage;
- a new frontend framework;
- browser-specific File System Access API integration.

Those can be layered on after folder selection, parsing, preview, confirmation, and persistence work reliably end to end.
