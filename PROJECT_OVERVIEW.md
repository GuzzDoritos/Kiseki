# Kiseki Project Overview

## What Kiseki is

Kiseki is a local-first Japanese immersion tracker. It keeps a library of media, records reading or viewing sessions, calculates progress, imports reading history from TTSU, and can attach trusted metadata from Jiten.

The current product is centered on books. The domain already models books, anime, and games, but the complete import and Jiten workflows currently support books only.

This document describes the repository as it exists now, including which screens are functional and which are still presentation scaffolds.

## The 30-second mental model

```text
                         +------------------+
TTSU statistics files ->|                  |
                         |   Kiseki.Core    |-> SQLite: kiseki.db
Jiten public API <------>| entities/rules/  |
                         | integrations     |
                         +---------+--------+
                                   ^
                         +---------+---------+
                         |                   |
                  Kiseki.Web          Kiseki.Console
                  Razor Pages         Spectre.Console
```

Both applications use the same Core project and the same per-user SQLite database. The Web project is the main visual interface. The Console project is also usable and currently exposes some editing and series operations that the Web interface has not implemented yet.

The central object is `MediaWork`: one concrete item such as a light-novel volume, anime season, or game. It owns immersion logs and may belong to a series. Its character total can come from Jiten or from a manual override.

## Solution structure

The solution file is `Kiseki.slnx` and contains four .NET 10 projects.

| Project | Role | Depends on |
| --- | --- | --- |
| `Kiseki.Core` | Entities, EF Core database model, migrations, TTSU parsing/import rules, and Jiten client/mapping | EF Core and SQLite |
| `Kiseki.Web` | ASP.NET Core Razor Pages interface | `Kiseki.Core` |
| `Kiseki.Console` | Interactive terminal interface built with Spectre.Console | `Kiseki.Core` |
| `Kiseki.Tests` | xUnit unit and integration-style tests | Core and Web |

The dependency direction is intentional: Core does not reference either front end. Shared business rules should live in Core, while HTML formatting belongs in Web and terminal presentation belongs in Console.

## Technology stack

- .NET 10 with nullable reference types and implicit usings enabled.
- ASP.NET Core Razor Pages for the Web application.
- Entity Framework Core 10 with SQLite for persistence.
- Spectre.Console for the terminal application.
- xUnit for tests.
- Bootstrap, a custom dark-theme stylesheet, and a small amount of vanilla JavaScript in Web.
- `HttpClient` plus `System.Text.Json` for Jiten.
- A local `dotnet-ef` 10.0.11 tool manifest for migrations.

There is no authentication, server account, or cloud database. Kiseki currently behaves as a single-user desktop/local web application.

## Domain model

### Relationship hierarchy

```text
Franchise
  1 -> many MediaSeries
             1 -> many MediaWork
                         1 -> many ImmersionLog
```

The hierarchy separates different meanings:

- A `Franchise` is the broadest grouping, such as all Re:Zero adaptations. It may retain a Jiten anchor deck because Jiten represents franchises as connected deck graphs rather than a single franchise ID.
- A `MediaSeries` groups works of one `MediaType`, such as the Re:Zero light novels. It can belong to a franchise and may point at a parent Jiten deck.
- A `MediaWork` is the actual library entry whose progress is tracked, such as volume 1.
- An `ImmersionLog` is one dated session attached to a work.

### `MediaWork`

`MediaWork` is the main aggregate used by both applications. Its persisted state includes:

- `Id` and `Title`;
- `MediaType`: `Book`, `Anime`, or `Game`;
- optional `MediaSeriesId` and navigation to `MediaSeries`;
- optional Jiten parent deck ID, subdeck ID, character count, and cover URL;
- optional manual character-count override;
- explicit completed status;
- a collection of immersion logs.

Important derived rules:

```text
TotalCharacters = ManualCharacterCountOverride
                  ?? JitenCharacterCount
                  ?? 0

CurrentCharactersRead = sum of all log character counts

Progress = 100% when explicitly completed
           otherwise CurrentCharactersRead / TotalCharacters
           capped at 100%
```

A manual total therefore wins over Jiten without deleting the Jiten value. Marking a work complete forces its displayed progress to 100 percent.

The `LinkToJitenDeck`, `LinkToJitenSubdeck`, and `RemoveJitenLink` methods keep the Jiten-owned fields synchronized. IDs must be positive, counts cannot be negative, and persisted covers must be absolute HTTPS URLs no longer than 2,048 characters. `nocover.jpg`, blank values, and unsafe URLs become `null`.

### `ImmersionLog`

An immersion log stores:

- a `DateOnly` date;
- characters read;
- time spent in minutes;
- a source string, currently usually `ttsu`.

There is no separate session service or manual-session Web form yet. TTSU is currently the main producer of logs.

### Series and franchises

`MediaSeries` and `Franchise` validate non-empty titles. Deleting a series does not delete its works; EF sets the affected `MediaSeriesId` values to null. Deleting a franchise similarly detaches its series rather than deleting them.

The Console application can create and assign series. The Web series pages are still scaffolds. Franchise persistence and the Jiten franchise DTO/client exist, but there is no active franchise workflow yet.

## Persistence and migrations

### Database location

The default database is:

```text
<LocalApplicationData>/kiseki.db
```

On Windows this is normally under `%LOCALAPPDATA%`. `ImmersionDbContext` uses this path when constructed without injected options, and the Web startup configures the same path explicitly. This is why the Console and Web applications see the same library.

Tests inject an in-memory SQLite connection instead, so they do not touch the user's database.

### Startup behavior

- Web calls `Database.MigrateAsync()` during startup.
- Console calls `DatabaseInitializer.MigrateAsync()`.
- The Console initializer copies an existing database to a timestamped backup before applying pending migrations.
- Web currently migrates directly without making that backup.

### Migration history

1. `InitialCreate` created media works and immersion logs.
2. `NewTable` renamed the log time column from seconds to minutes.
3. `AddFranchisesSeriesAndJitenSubdeckLinks` added media types, franchises, series, subdeck links, indexes, relationships, and the subdeck-parent check constraint.
4. `AddJitenCoverUrlToMediaWorks` added the nullable persisted cover URL.

The schema indexes Jiten identifiers and relationship foreign keys. A check constraint prevents a Jiten subdeck ID from existing without a parent deck ID.

## Main data flows

### TTSU import

TTSU supplies per-book `statistics*.json` files. The important path is:

```text
statistics JSON
  -> TtsuDataLoader
  -> TtsuBookContainer / TtsuReaderDTO
  -> TtsuSessionMapper
  -> TtsuBookImporter
  -> MediaWork + ImmersionLog rows
```

#### Parsing rules

`TtsuDataLoader`:

- deserializes the JSON entry list;
- validates `yyyy-MM-dd` dates;
- rejects negative character counts;
- rejects negative, infinite, or NaN reading times;
- uses the first non-empty JSON title, then the containing folder name as fallback;
- recognizes filenames beginning with `statistics`, case-insensitively.

TTSU's `readingTime` is treated as seconds. `TtsuSessionMapper` divides it by 60 before storing `TimeSpentMinutes`.

When several entries represent the same date, `TtsuBookImporter` keeps the entry with the greatest `LastStatisticModified` value, with later input position as a tie-breaker.

#### Web TTSU flow

`/Import/Ttsu` is a two-step preview/confirm workflow.

1. The browser folder picker uploads the selected files.
2. The server filters for statistics files and validates size/count limits.
3. It parses the books without changing the database.
4. A preview batch is kept in the in-memory cache for 30 minutes.
5. The user chooses books and whether each matching title should merge or create a duplicate.
6. Confirm applies all selected changes in one database transaction.

Limits are 250 statistics files, 8 MB per statistics file, and 64 MB for the request.

Matching is based on a normalized title: trim, collapse repeated whitespace, and compare uppercase text. A merge updates existing `ttsu` logs on matching dates and adds new dates. Logs from other sources remain untouched. A create operation always produces a new work.

The preview is intentionally temporary. It disappears after 30 minutes or when the Web process restarts because it is stored in memory, not SQLite.

The folder picker uses the browser's `webkitdirectory` behavior. JavaScript filters the visible selection when supported, but the server always performs its own filtering and validation.

#### Console TTSU flow

The Console asks for a root folder containing one subdirectory per book. It picks the newest statistics file in each book directory and lets the user import one selected book.

`KISEKI_TTSU_DATA_PATH` can provide the default folder path.

Unlike the Web workflow, the current Console TTSU path always creates a new work; it does not offer the Web merge behavior.

### Jiten integration

The Jiten client uses the public `https://api.jiten.moe/` API.

`JitenApiClient` currently supports:

- book search through `get-media-decks`;
- paginated parent/subdeck details;
- the connected franchise graph.

Search is hard-coded to Jiten media type `4`, which is its novel/book category. That is why both front ends currently reject Jiten linking for anime and games.

Search and detail pagination continue until the API's reported total has been loaded or a page is empty. Cancellation tokens pass through every HTTP call. The Web-configured client has a 15-second timeout.

#### Shared selection mapping

`JitenMediaSelection` converts API DTOs into a value that either front end can safely apply to a work.

- A whole deck stores its own ID and character count.
- A subdeck stores the parent as `JitenDeckId`, the child as `JitenSubdeckId`, and the child's count.
- A child cover is preferred; the parent cover is the fallback.
- The display title prefers original, then English, then romaji.
- A title choice can keep the current local title or explicitly use one of Jiten's three variants.

#### Web linking flow

The operational Web entry point is a book's details page, not the scaffolded `/Import/Jiten` page.

```text
/Library
  -> /Library/Details/{workId}
  -> Link to Jiten
  -> /Library/LinkJiten/{workId}
  -> search
  -> choose entire deck or load a subdeck
  -> choose title behavior
  -> confirm
  -> redirect back to Details
```

Search and subdeck browsing are GET requests and do not write anything. The final action is an antiforgery-protected POST.

The POST accepts only the work ID, parent deck ID, optional subdeck ID, and title-choice enum. It fetches current Jiten details again and derives titles, character count, and cover from that response. Hidden browser fields are not trusted for metadata. An invalid or disappeared subdeck is rejected without changing the work.

Jiten network failures, timeouts, malformed responses, missing decks, and HTTP 429 responses are turned into page-level errors. A successful link uses Post/Redirect/Get and a TempData notice.

The Library and Details screens use the persisted Jiten values. They do not contact Jiten on every page view. Remote covers have a local visual fallback if the URL is missing or the image fails to load.

#### Console Jiten flow

Console can add a new book directly from Jiten or link an existing book. It supports selecting a whole parent deck or drilling into its subdecks. Linking an existing subdeck can also populate the current series' parent Jiten deck after confirmation.

## Web application

### Startup and services

`Kiseki.Web/Program.cs` registers:

- Razor Pages;
- `ImmersionDbContext` with SQLite;
- a typed `IJitenApiClient`;
- `TtsuDataLoader`;
- an in-memory `ITtsuImportBatchStore`.

It applies migrations before serving requests, uses the normal production exception handler and HSTS outside Development, serves static assets, and maps Razor Pages.

### Page status

| Route | Status | Purpose |
| --- | --- | --- |
| `/` | Scaffold | Dashboard layout exists, but its statistics are placeholders. |
| `/Library` | Operational | Loads works, series, logs, progress, covers, and filters by query/type/status. |
| `/Library/Details/{id}` | Operational | Shows real work metadata, effective progress, and all immersion sessions. |
| `/Library/LinkJiten/{id}` | Operational for books | Searches Jiten and links a whole deck or subdeck. |
| `/Library/Edit/{id}` | Scaffold | Form is visible, but its save button and persistence are disabled. |
| `/Import` | Operational navigation | Lets the user choose an import source. |
| `/Import/Ttsu` | Operational | Folder upload, preview, merge/create, and transactional confirmation. |
| `/Import/Jiten` | Scaffold | Direct Web creation from Jiten is not implemented. |
| `/Series` and child routes | Scaffold | Layout/forms exist, but they do not read or write Core data. |
| `/Error`, `/Privacy` | Basic framework pages | General supporting pages. |

### Library behavior

The Library query eagerly loads series and logs, then applies:

- title search;
- optional media-type filtering;
- status filtering for completed, in progress, or not started;
- ordering by completion and title.

Rows show title, series, media type, source badges, sessions, progress, status, and a persisted Jiten cover when available. Clicking a row opens the details page.

The Details PageModel uses `AsNoTracking`, loads series and logs, returns HTTP 404 for an unknown ID, and maps the entity to a presentation model. Logs are sorted newest first and then by ID for deterministic ordering.

### Presentation layer

Web-specific records in `Kiseki.Web/Models` prepare status labels, duration formatting, progress bars, details, and TTSU previews. This keeps HTML formatting out of Core entities.

`Pages/Shared` contains the layout, sidebar, media row, progress bar, and validation partials. The custom stylesheet is organized around the application shell, Library, common page primitives, Jiten linking, and TTSU import. Responsive rules collapse the sidebar and content grids on smaller screens.

`wwwroot/js/site.js` has two focused responsibilities:

- improve TTSU folder selection feedback and client-side file filtering;
- replace failed remote cover images with the local media placeholder.

Bootstrap and jQuery assets are vendored under `wwwroot/lib`. Google Fonts and Jiten cover images are fetched remotely by the browser.

## Console application

Console uses manual composition in `Program.cs`; there is no dependency-injection container.

At startup it opens the shared database, backs it up if migrations are pending, applies migrations, constructs the screens, and enters a Spectre.Console menu loop.

Current Console capabilities:

- add a book from a TTSU directory;
- add a book directly from Jiten;
- view the library in a table;
- edit title, media type, series, Jiten link, manual total, and completion status;
- create or assign a compatible series;
- remove a Jiten link;
- delete a work and its logs.

Anime and game import are announced but not implemented. The entity types can still exist, and an existing work's media type can be changed in Console.

## Tests

`Kiseki.Tests` currently covers the important completed slices without contacting the live Jiten service.

- `MediaWorkTests`: progress/link invariants, manual precedence, unlinking, and cover safety.
- `JitenMediaSelectionTests`: parent/subdeck mapping, cover fallback, and title application.
- `JitenApiClientTests`: paginated detail loading with a stub HTTP handler.
- `LibraryJitenPageTests`: Web linking, authoritative refetch, tamper rejection, GET search, subdeck loading, details projection, log order, and 404 behavior.
- `TtsuDataLoaderTests`: file discovery, JSON parsing, fallback titles, validation, and newest-file selection.
- `TtsuSessionMapperTests`: date parsing and seconds-to-minutes conversion.
- `TtsuBookImporterTests`: repeated-date collapse, merging, and title normalization.
- `TtsuImportPageTests`: preview isolation, confirm, reimport, file rejection, selection, and expired batches.
- `DatabaseMigrationTests`: migration preservation and relationship delete behavior.

PageModel tests use in-memory SQLite. Jiten client tests use a custom `HttpMessageHandler`, making the suite deterministic and safe to run offline.

At the time this overview was written, the suite contains 36 passing tests.

## Running the project

Install the .NET 10 SDK, then from the repository root:

```powershell
dotnet restore Kiseki.slnx
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
```

Run Web:

```powershell
dotnet run --project Kiseki.Web
```

The launch profiles use `http://localhost:5051` and `https://localhost:7169` in Development.

Run Console:

```powershell
dotnet run --project Kiseki.Console
```

Both commands use the same local database. Stop and restart a running application after changing compiled code or adding a migration.

## Working with migrations

Restore the repository-local tool when necessary:

```powershell
dotnet tool restore
```

Create a migration using Console as the startup project:

```powershell
dotnet tool run dotnet-ef migrations add MigrationName `
  --project Kiseki.Core `
  --startup-project Kiseki.Console
```

Normal application startup applies pending migrations. Be conscious that a manual EF database update without a test connection targets the default per-user database.

Whenever the entity model changes, commit the migration class, generated designer, and `ImmersionDbContextModelSnapshot.cs` together.

## Where to make common changes

| Goal | Main files |
| --- | --- |
| Change progress or metadata invariants | `Kiseki.Core/Entities/MediaWork.cs` |
| Change relationships or storage | `Kiseki.Core/ImmersionDbContext.cs` and migrations |
| Change TTSU validation/parsing | `TtsuDataLoader.cs` and `TtsuSessionMapper.cs` |
| Change TTSU create/merge semantics | `TtsuBookImporter.cs` |
| Change Jiten endpoints/pagination | `IJitenApiClient.cs` and `JitenApiClient.cs` |
| Change how a Jiten result maps to a work | `JitenMediaSelection.cs` |
| Change Web TTSU workflow | `Pages/Import/Ttsu.cshtml(.cs)` and Web import models/store |
| Change Web Jiten linking | `Pages/Library/LinkJiten.cshtml(.cs)` |
| Change Library list/details | `Pages/Library`, shared row/progress partials, and Web view models |
| Change terminal behavior | `Kiseki.Console/Screens` and `Display` |
| Change global Web appearance/navigation | `Pages/Shared`, `wwwroot/css/site.css`, and `site.js` |

When changing a Razor Page route value, keep its generated query name aligned with the handler parameter. For example, the subdeck link sends `deckId` because `OnGetSubdecksAsync` accepts `int deckId`.

## Important current limitations

- The application is single-user and local only.
- The database path is fixed to the per-user Local Application Data directory.
- Web migration startup does not create the safety backup that Console creates.
- Jiten linking is book-only and searches Jiten's novel media type.
- Direct Jiten import in Web is still a scaffold; Web links Jiten to an existing work through Details.
- Dashboard, Web editing, and all Web series pages are still scaffolds.
- There is no Web UI to delete works, unlink Jiten, refresh linked metadata, add manual sessions, or mark completion.
- Franchise DTOs/entities and Jiten franchise loading are not connected to a product workflow.
- `AniListMediaDTO` is an empty placeholder.
- `MediaAggregator` is an early helper and is not currently called by either front end.
- Series membership has no explicit work ordering/volume-number field.
- Library currently loads full log collections to calculate list progress; this is acceptable for a small local library but may need database projections at larger scale.
- TTSU preview state is process-local and temporary.
- Jiten is an external dependency for search/link actions, although already-linked Library pages remain local.

## Design principles already visible in the code

- Keep reusable rules in Core and front-end presentation in its own project.
- Use domain methods when several fields must change as one logical operation.
- Treat external search results as previews and verify them again before saving.
- Keep read-only queries untracked and use tracked entities only for mutations.
- Pass request cancellation through database and HTTP operations.
- Preview imports before writing and commit multi-book imports transactionally.
- Preserve local/user-owned values unless the user explicitly chooses to replace them.
- Prefer graceful fallbacks for unavailable external services and images.

## Suggested next development sequence

The clearest next vertical slices are:

1. Make Web Edit load and save a real work, including manual total and completion.
2. Add Web create/edit/details support for series and assignment from a work.
3. Reuse the existing Jiten selection flow for direct Web import.
4. Add manual immersion-session creation and editing.
5. Make the Dashboard aggregate real log data.
6. Connect franchises and expand Jiten media-type mappings for anime and games.
7. Add configurable storage/backup behavior before treating Web as a long-running hosted service.

That order rounds out the existing entities and screens before introducing substantially new concepts.
