# Agent Guide: Kiseki

This document provides durable context, architectural conventions, and design constraints to help AI agents work safely and productively in the Kiseki repository.

Agents working in this codebase should preserve the established architecture, layered dependencies, and existing scope unless a task explicitly requests changes.

---

## 1. System Overview & Architecture

Kiseki is a local-first Japanese immersion tracker that logs reading and media consumption, calculates progress, imports reading histories from TTSU (a web-based reader), and fetches metadata from Jiten (a Japanese media database API).

The system consists of a single solution (`Kiseki.slnx`) targeting **.NET 10** across four projects:

```text
               +------------------------------------------+
               |               Kiseki.Core                |
               |  Entities, DbContext, Migrations,       |
               |  TTSU Parsers, Jiten API Client & Models |
               +--------------------+---------------------+
                                    ^
                    +---------------+---------------+
                    |                               |
         +----------+----------+         +----------+----------+
         |     Kiseki.Web      |         |    Kiseki.Console   |
         | ASP.NET Core Razor  |         |   Spectre.Console   |
         | Pages Web Interface |         | Terminal Application|
         +---------------------+         +---------------------+

               +------------------------------------------+
               |               Kiseki.Tests               |
               |      xUnit Test Suite (In-Memory DB)     |
               +------------------------------------------+
```

### Dependency Rules
- **`Kiseki.Core`** has no dependencies on front-end libraries, UI frameworks, or ASP.NET Core web abstractions. Shared business logic, database configuration, invariants, and external API clients belong exclusively here.
- **`Kiseki.Web`** and **`Kiseki.Console`** both depend on `Kiseki.Core` and consume the same per-user SQLite database.
- **`Kiseki.Tests`** references `Kiseki.Core` and `Kiseki.Web` to verify domain invariants, API client integration (via HTTP handlers), and page models.

---

## 2. Domain Model & Invariants

### Hierarchy
```text
Franchise (e.g. "Re:Zero")
  └── 1 : N ── MediaSeries (e.g. "Re:Zero Light Novels" [Book])
                 └── 1 : N ── MediaWork (e.g. "Re:Zero Volume 1")
                                └── 1 : N ── ImmersionLog (e.g. 2026-08-05, 5,420 chars)
```

### Core Entities (`Kiseki.Core.Entities`)

1. **`MediaWork`** (Aggregate Root):
   - Represents a single trackable library item (a volume, game, or anime season).
   - Currently focused on `MediaType.Book`.
   - **Progress & Character Count Calculation**:
     - `TotalCharacters = ManualCharacterCountOverride ?? JitenCharacterCount ?? 0`
     - `CurrentCharactersRead = Logs.Sum(l => l.CharactersRead)`
     - `ProgressPercentage`: If `IsCompleted == true`, progress is **100%**. If `TotalCharacters == 0`, progress is **0%**. Otherwise: `Math.Min(100.0, (CurrentCharactersRead / TotalCharacters) * 100.0)`.
   - **Jiten Linking**:
     - Controlled through `LinkToJitenDeck(...)`, `LinkToJitenSubdeck(...)`, and `RemoveJitenLink()`.
     - Ensures valid positive IDs, non-negative character counts, and valid HTTPS cover URLs (max 2,048 characters).
     - Database enforces check constraint `CK_MediaWorks_JitenSubdeckRequiresDeck` (`JitenSubdeckId IS NULL OR JitenDeckId IS NOT NULL`).

2. **`ImmersionLog`**:
   - Represents an immersion session with `Date` (`DateOnly`), `CharactersRead` (`int`), `TimeSpentMinutes` (`double`), and `Source` (`string`, defaults to `"ttsu"`).
   - Relationship to `MediaWork` is configured via `MediaWork.Logs`.

3. **`MediaSeries`**:
   - Groups works of a specific `MediaType`. Deleting a series does **not** delete its works (foreign key `MediaSeriesId` is set to `null`).

4. **`Franchise`**:
   - High-level grouping across media types. Uses an optional `JitenAnchorDeckId` to anchor to Jiten's connected deck graph. Deleting a franchise sets `FranchiseId` on child series to `null`.

---

## 3. Persistence & Database Conventions

- **Database Engine**: PostgreSQL (Neon serverless PostgreSQL) in production; SQLite is also supported locally via `DatabaseProvider=sqlite`. Shared connection normalization, retry behavior, and startup upgrades live in Core.
- **Connection Configuration**: Configured via standard ASP.NET Core `ConnectionStrings:DefaultConnection` (or `ConnectionStrings__DefaultConnection` environment variable on Render and local `.env`).
- **Migrations**:
  - Located in `Kiseki.Core/Migrations`.
  - Applied automatically on application startup via `context.Database.MigrateAsync()`.
  - When modifying entities, migrations must be generated using the local `dotnet-ef` tool:
    ```powershell
    dotnet tool run dotnet-ef migrations add <MigrationName> --project Kiseki.Core --startup-project Kiseki.Core
    ```
- **In-Memory Testing**: `Kiseki.Tests` uses in-memory SQLite with `EnsureCreatedAsync()` for isolated, instant test execution without external network dependencies.
- **Existing local SQLite databases**: `SqliteSchemaUpgrade` applies additive import-state changes to databases previously provisioned with `EnsureCreatedAsync()`. Do not apply PostgreSQL migrations to SQLite or recreate existing local databases.
- **PostgreSQL integration tests**: Set `KISEKI_TEST_POSTGRES` to a disposable local PostgreSQL server to run migration/concurrency tests. They create isolated test databases and refuse remote hosts.
- **Query Guidelines**:
  - Read-only queries must use `.AsNoTracking()`.
  - When loading `MediaWork` for progress calculation or details, eagerly load `.Include(w => w.Logs)` and `.Include(w => w.MediaSeries)`.
  - Mutations must load tracked entities.

---

## 4. Integrations & External Data Flows

### TTSU Reading Ingestion
- **Formats**: TTSU exports per-book JSON files (`statistics*.json`).
- **Key Pipeline**:
  1. `TtsuDataLoader`: Reads directory or input stream, deserializes `TtsuReaderDTO`, validates `yyyy-MM-dd` dates, character counts, and durations.
  2. `TtsuSessionMapper`: Converts reading time from seconds to minutes (`readingTime / 60d`).
  3. `TtsuStatisticsNormalizer` reconciles multiple files within one source folder/title per day. Newest known revisions win; unknown revisions and conflicting ties remain reviewable candidates.
  4. `TtsuMergePlanner` produces immutable daily decisions and before/after totals. `TtsuImportService` owns matching, tracked mutations, serializable commits, and durable operation receipts for both Web and Console.
     - `ImmersionLog.SourceRevision` preserves source modification timestamps. Legacy rows start unknown; reviewed baseline adoption preserves IDs. Missing dates and non-TTSU logs are retained.
     - `TtsuBinding` uses the work ID as its key, stores the original normalized source title/folder hint, and carries a concurrency token. A unique `(TtsuBindingId, Date)` index applies to bound daily rows; a check constraint enforces source/work consistency.
     - Match persisted source hints before suggesting normalized-title candidates. Ambiguity requires explicit selection; source renames/moves are not globally identifiable.
     - Duplicate legacy days and orphan-log assignments require explicit review. Never sum duplicate snapshots or choose a winner silently.
     - `TtsuBookImporter` remains a convenience for conflict-free in-memory creation/merge; interactive/persistent imports use `TtsuImportService`.
- **Web Import Workflow**:
  - Uses `ITtsuImportBatchStore` (an in-memory cache with 30-minute TTL).
  - Multi-step: Folder select -> server parse -> temporary preview -> target/conflict choices -> refreshed review -> single transactional commit. Reviewed fingerprints are stored server-side; changes between preview and commit require review again. Expired previews require re-upload; committed receipts survive restarts and prevent replayed POSTs from duplicating work.

### Jiten.moe Integration
- **API Client**: `IJitenApiClient` / `JitenApiClient` (`https://api.jiten.moe/`).
- **Media Support**: Currently restricted to books (`mediaType=4`).
- **Pagination**: Automatically paginates search results and deck detail/subdecks.
- **Authoritative Selection & Refetch**:
  - `JitenMediaSelection`: Bridges Jiten DTOs to domain values (`DisplayTitle`, cover fallback, title choices).
  - When linking in Web (`LinkJiten`), the client **never trusts hidden form metadata**. On POST, the server re-fetches the deck detail from Jiten to ensure accurate counts and valid subdeck hierarchies before applying changes.

---

## 5. Web Frontend Conventions (`Kiseki.Web`)

- **Technology**: ASP.NET Core Razor Pages, Bootstrap 5 (CSS only for layout/grid), custom dark-mode design system in `site.css`, vanilla JavaScript in `site.js`.
- **View Models**: All pages project domain entities into immutable view models/records in `Kiseki.Web/Models` (e.g. `MediaWorkListItemViewModel`, `MediaWorkDetailsViewModel`, `ProgressBarViewModel`).
- **Partial Views**:
  - `_MediaWorkRow.cshtml`: Consistent row layout for library items.
  - `_ProgressBar.cshtml`: Accessible progress bar with `aria-valuenow`, `aria-valuemin`, `aria-valuemax`.
  - `_Sidebar.cshtml`: Navigation bar.
- **Images & Fallbacks**: Cover images use `loading="lazy"` and `referrerpolicy="no-referrer"`. `site.js` replaces broken remote cover images with the media type's Japanese character glyph (`本`, `アニメ`, `ゲーム`).
- **Flash Notices**: Success feedback uses `TempData["LibraryNotice"]` followed by the Post/Redirect/Get pattern.

---

## 6. Testing Conventions (`Kiseki.Tests`)

- **Framework**: xUnit.
- **Isolation**:
  - Database tests use an isolated in-memory SQLite connection (`SqliteConnection("Data Source=:memory:")`).
  - Web page tests instantiate PageModels with `TestDatabase` fixtures and stub HTTP clients.
  - Tests do **not** hit live external endpoints (`api.jiten.moe`).
- **Standard Verification**:
  ```powershell
  dotnet test Kiseki.slnx
  ```
  All tests must pass before completing tasks.

---

## 7. Working Rules for Future Agents

1. **Preserve Current Scope**:
   - Focus on improving, consolidating, and finishing the existing vertical slice (Book tracking, TTSU import, Jiten linking, Library list/details, Console maintenance).
   - Do not unilaterally introduce unrelated features (e.g. cloud sync, multi-user auth, anime scrapers) unless explicitly instructed.
2. **Keep Business Logic in Core**:
   - Do not implement business rules or calculations directly inside Razor views or PageModels if they belong to domain entities or Core services.
3. **Respect Established Error Handling**:
   - Network calls to external APIs must pass `CancellationToken` and map exceptions to friendly user-facing messages.
   - External inputs (files, URLs, IDs) must be validated before persisting.
4. **Follow EF Migration Disciplines**:
   - Always verify that entity model changes are accompanied by generated migrations and updated snapshot files.
