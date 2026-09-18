# Antigravity execution prompt: Stage 6A

Paste everything below into Antigravity.

---

Continue the automatic TTSU metadata work in the current Kiseki working tree.

Stages 0 through 4 and the Stage 5 calibration corpus are implemented and verified but remain uncommitted. Preserve all of those changes and every unrelated user file. Do not reset, revert, recreate, or commit them.

The Stage 5 no-preference auto-match default intentionally remains **off** because the required interactive multi-book browser review was not completed. Do not enable it in this stage.

**Implement only Stage 6A now: provider-neutral cover persistence and provenance.** Do not add Amazon Japan, BookWalker, retailer scraping, image downloading, or any external cover-resolution service. External-provider work is Stage 6B and requires a separate explicit provider/usage-policy decision.

Do not return another plan or approval checklist. Make the domain, migration, SQLite-upgrade, application, UI, and test changes; run verification; inspect the final diff; and provide the completion report requested below.

## Read before editing

Read these files completely:

1. `AGENTS.md`
2. `docs/automatic-import-implementation-plan.md`, especially Stage 6 and the final acceptance checklist
3. `AUTOMATIC_IMPORT_DESIGN.md` for product direction only; it is not authorization to scrape or use a provider
4. `Kiseki.Core/Entities/MediaWork.cs`
5. `Kiseki.Core/Models/JitenMediaSelection.cs`
6. `Kiseki.Core/Models/Metadata/JitenMatchCandidate.cs`
7. `Kiseki.Core/Services/TtsuImportService.cs`
8. `Kiseki.Core/ImmersionDbContext.cs`
9. `Kiseki.Core/Services/SqliteSchemaUpgrade.cs`
10. every file under `Kiseki.Core/Migrations/`
11. `Kiseki.Web/Models/MediaWorkListItemViewModel.cs`
12. `Kiseki.Web/Models/MediaWorkDetailsViewModel.cs`
13. `Kiseki.Web/Pages/Library/Index.cshtml.cs`
14. `Kiseki.Web/Pages/Library/Details.cshtml.cs`
15. `Kiseki.Web/Pages/Library/Details.cshtml`
16. `Kiseki.Web/Pages/Library/LinkJiten.cshtml.cs`
17. `Kiseki.Web/Pages/Library/LinkJiten.cshtml`
18. `Kiseki.Web/Pages/Shared/_MediaWorkRow.cshtml`
19. `Kiseki.Web/Pages/Shared/_MediaWorkCard.cshtml`
20. `Kiseki.Web/Pages/Import/Ttsu.cshtml.cs`
21. `Kiseki.Web/wwwroot/js/components/cover-editor.js`
22. `Kiseki.Web/wwwroot/js/components/cover-fallback.js`
23. the Console files that reference `JitenCoverUrl`, Jiten linking, or cover display
24. `Kiseki.Tests/MediaWorkTests.cs`
25. `Kiseki.Tests/JitenMediaSelectionTests.cs`
26. `Kiseki.Tests/LibraryDetailsPageTests.cs`
27. `Kiseki.Tests/LibraryJitenPageTests.cs`
28. `Kiseki.Tests/TtsuImportPageTests.cs`
29. `Kiseki.Tests/TtsuMergeServiceTests.cs`
30. `Kiseki.Tests/TtsuSchemaUpgradeTests.cs`
31. `Kiseki.Tests/TtsuPostgreSqlTests.cs`
32. `docs/ttsu-merge-manual-testing.md`

Inspect `git status --short` and the complete diff before editing. Treat the detailed implementation plan as the repository-grounded contract.

Baseline after the Stage 5 review is:

```text
dotnet build Kiseki.slnx: succeeded, 0 warnings, 0 errors
dotnet test Kiseki.slnx: 350 passed, 4 skipped, 0 failed, 354 total
focused calibration + TTSU page slice: 93 passed, 0 failed
calibration corpus: 31 cases; 17 High, 11 Review, 3 None; zero false High
dotnet-ef migrations has-pending-model-changes: no pending changes
git diff --check: clean
```

The four skipped tests are the opt-in PostgreSQL integration tests requiring `KISEKI_TEST_POSTGRES`. Do not report them as passed when that variable is absent.

## Why Stage 6 is split

The master plan requires two distinct things:

1. generalize the current misleading `JitenCoverUrl` persistence and preserve user/legacy covers;
2. only then integrate a non-Jiten provider after its API, permitted usage, edition identity, hotlink/cache policy, and image-validation requirements are approved.

This prompt authorizes item 1 only. Do not create placeholder Amazon/BookWalker clients, provider interfaces that nothing uses, guessed endpoints, HTML parsers, ISBN mappings, or speculative image infrastructure.

## Stage 6A objective

Replace the Jiten-specific persisted-cover concept with a generic cover URL plus durable provenance while preserving every existing cover value.

The resulting domain must distinguish:

- no cover;
- a legacy cover whose origin is unknown;
- a volume-specific Jiten cover;
- a Jiten parent/series fallback;
- a user-entered override.

User overrides and migrated legacy covers are protected. Jiten linking, relinking, unlinking, automatic TTSU enrichment, and future metadata operations must not accidentally erase or replace them.

## Domain model

Add a Core enum with stable explicit values, for example:

```csharp
public enum MediaCoverSource
{
    None = 0,
    LegacyUnknown = 1,
    JitenSpecific = 2,
    JitenParentFallback = 3,
    UserOverride = 4
}
```

Use an equivalent name only if it is clearer and consistently applied. Do not add unapproved provider values.

Generalize `MediaWork`:

- replace the public domain property `JitenCoverUrl` with `CoverUrl`;
- add a persisted `CoverSource`;
- retain the HTTPS-only, trimmed, maximum-2,048-character invariant;
- reject or normalize away `nocover.jpg` exactly as today;
- expose small computed properties such as `HasCover` and `IsCoverProtected` where they remove duplicated policy checks;
- `CoverUrl == null` must imply `CoverSource == None`;
- a non-null `CoverUrl` must have a non-`None` source;
- `LegacyUnknown` and `UserOverride` are protected sources.

Do not retain a second writable `JitenCoverUrl` alias or two competing cover columns. One URL and one provenance value must be authoritative after migration.

## Cover mutation semantics

Keep cover policy inside the aggregate instead of scattering it through PageModels.

Required behavior:

1. `UpdateCoverUrl(...)` remains the manual editor entry point and must set `CoverSource = UserOverride`.
2. `JitenMediaSelection.ApplyTo(...)` maps evidence explicitly:
   - `Specific` -> `JitenSpecific`;
   - `ParentFallback` -> `JitenParentFallback`;
   - `None` or a rejected URL -> no new cover.
3. Linking or relinking Jiten may set/replace a Jiten-derived cover when the work has no cover or its current cover is already Jiten-derived.
4. Linking or relinking Jiten must preserve `LegacyUnknown` and `UserOverride` covers while still updating the requested Jiten IDs and character count.
5. `RemoveJitenLink()` must clear Jiten IDs and Jiten character count.
6. `RemoveJitenLink()` must clear a `JitenSpecific` or `JitenParentFallback` cover because it came from that link.
7. `RemoveJitenLink()` must retain `LegacyUnknown` and `UserOverride` covers.
8. Automatic TTSU metadata application must still skip an existing work with any cover, regardless of source.
9. A new automatically imported work may receive the fresh Jiten cover with the correct Jiten provenance.
10. No cover mutation may change titles, logs, TTSU totals, manual totals, completion, grouping, or receipt behavior.

Avoid a boolean that can disagree with an enum. `IsCoverProtected` should be derived from the source rather than independently persisted.

Keep existing method signatures source-compatible where practical through optional parameters or focused overloads, but do not let an omitted provenance argument silently classify a manual URL as Jiten. All production call sites must pass or derive the correct source.

## Database migration and constraints

Generate the next PostgreSQL EF migration with the local tool, using a clear name such as:

```powershell
dotnet tool run dotnet-ef migrations add GeneralizeMediaWorkCoverProvenance --project Kiseki.Core --startup-project Kiseki.Core
```

The migration must preserve data:

- rename `MediaWorks.JitenCoverUrl` to `CoverUrl`; do not drop and recreate the value;
- add non-null `CoverSource` with a safe default;
- backfill every existing non-null cover as `LegacyUnknown`;
- leave existing null covers as `None`;
- retain the 2,048-character limit;
- add a database check constraint enforcing URL/source consistency;
- update `ImmersionDbContextModelSnapshot` through the EF tool.

If EF scaffolding guesses a destructive drop/add instead of a rename, correct the generated migration to use `RenameColumn` and an explicit data update. Do not hand-edit the snapshot.

Use a check equivalent to:

```text
(CoverUrl IS NULL AND CoverSource = None)
OR
(CoverUrl IS NOT NULL AND CoverSource <> None)
```

Use provider-appropriate quoted identifiers and integer enum values. The `Down` migration must preserve the URL while renaming it back, even though provenance is necessarily discarded.

## Existing SQLite databases

Update `SqliteSchemaUpgrade` for both fresh and previously created local databases:

- fresh schemas expose `CoverUrl` and `CoverSource` through the EF model;
- if an old database has `JitenCoverUrl` but not `CoverUrl`, rename the column without losing values;
- add `CoverSource` idempotently when missing;
- backfill non-null migrated covers to `LegacyUnknown` and null covers to `None`;
- repeated startup upgrades must be safe;
- do not recreate `MediaWorks` or lose works, logs, links, totals, series assignments, or covers;
- do not run PostgreSQL migrations against SQLite.

SQLite's supported rename/add behavior may be used, but inspect the actual provider version used by this solution. Use exact column discovery rather than catching arbitrary SQL failures.

Because SQLite cannot add every table constraint to an existing table without rebuilding it, enforce the invariant in the domain and EF-created schema. Do not rebuild an existing local `MediaWorks` table merely to add the check constraint.

## Application and UI updates

Update every use of the old property to the generic cover state:

- Library list/card view models and projections use `CoverUrl`;
- Details uses `CoverUrl` and continues returning `{ coverUrl }` to the existing editor JavaScript;
- the manual editor sets `UserOverride`;
- Jiten link pages continue showing volume versus series-fallback evidence before linking;
- after persistence, the Details page shows a concise provenance label such as `Custom cover`, `Legacy cover`, `Jiten volume cover`, or `Jiten series fallback`;
- TTSU target protection checks any existing cover through the generic property/computed flag;
- `TtsuImportService` commit-time protection checks any existing cover;
- Console compiles and preserves the same link/unlink semantics, but gains no provider search or new workflow;
- broken remote images continue using the existing browser glyph fallback.

Do not expose enum integers in user-facing text. Keep the current cover editor request/response JSON shape unless a change is genuinely required.

## Required tests

Add or update tests covering at least:

### Domain behavior

- manual HTTPS cover becomes `UserOverride`;
- invalid, non-HTTPS, overlong, blank, and `nocover.jpg` manual values remain rejected;
- Jiten specific cover persists as `JitenSpecific`;
- parent fallback persists as `JitenParentFallback`;
- Jiten selection without a usable cover leaves URL null/source `None` on a new work;
- relinking replaces an existing Jiten-derived cover and provenance;
- relinking preserves `UserOverride` while updating link IDs/count;
- relinking preserves `LegacyUnknown` while updating link IDs/count;
- removing a link clears Jiten-derived covers;
- removing a link preserves `UserOverride` and `LegacyUnknown` covers;
- URL/source invariants cannot be put into an invalid combination through public domain methods.

### TTSU and Web regression

- automatic import of a new work persists the correct Jiten cover source;
- an existing work with any source of cover is protected and counted as a metadata skip;
- a concurrent cover addition of each protected source still lets reading import while skipping metadata;
- manual Details cover update persists `UserOverride` and returns the normalized generic URL;
- Details renders the correct provenance label without exposing raw enum numbers;
- list rows/cards continue displaying the generic cover URL and browser fallback attributes;
- manual Jiten linking preserves an existing protected cover;
- manual unlink behavior follows the source-specific rules;
- no Stage 0–5 matching, confidence, receipt, replay, or reading behavior regresses.

### Migration/provider behavior

- `EnsureCreated` creates `CoverUrl`, `CoverSource`, and the consistency constraint;
- the generated PostgreSQL migration upgrades a row with non-null `JitenCoverUrl` to the same `CoverUrl` plus `LegacyUnknown`;
- a null legacy cover becomes `None`;
- the SQLite additive upgrade preserves non-null and null legacy rows correctly;
- running the SQLite upgrader twice is safe;
- unrelated MediaWork fields and dependent rows survive the upgrade;
- the snapshot has no pending model changes;
- the four opt-in PostgreSQL concurrency/replay tests remain valid.

Use in-memory SQLite and stubs. Do not make live image, Jiten, retailer, or CDN requests.

## Stage 5 remains gated

Do not change:

- `AutoMatchMetadata` no-preference default (`false`);
- the local-storage default behavior;
- confidence thresholds or calibration expected results;
- the opaque candidate/review-token flow.

The 31-case calibration corpus must remain green. Stage 6A cover provenance is score-neutral.

## Out of scope — no Stage 6B provider work

Do not add:

- Amazon Product Advertising API or Amazon image-domain logic;
- BookWalker API/client/scraping;
- HTML parsing or browser automation for retailer pages;
- ISBN lookup or edition-matching heuristics;
- `ICoverResolutionService`, provider clients, or unused provider abstractions;
- image downloading, proxying, caching, blobs, or filesystem storage;
- redirect following, MIME sniffing, byte limits, dimension/aspect inspection, or Linux image libraries;
- background jobs, refresh schedulers, telemetry, or global caches;
- live-network tests.

Do not begin provider integration until the user separately approves:

- the exact provider/API;
- credentials and rate/usage terms;
- whether hotlinking or local caching is permitted;
- Japanese-edition identity requirements;
- retention and refresh policy.

## Verification

Run focused domain, Jiten, TTSU, Web, migration, and calibration tests while working. Then run exactly:

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
dotnet tool run dotnet-ef migrations has-pending-model-changes --project Kiseki.Core --startup-project Kiseki.Core
git diff --check
git status --short
```

Inspect the final diff for destructive cover migration, duplicate cover properties, lost legacy URLs, incorrect provenance, protected-cover overwrites, an enabled Stage 5 default, provider integration, live-network tests, or unrelated schema/UI changes.

## Completion report

Report:

- the final cover enum and aggregate invariants;
- link, relink, unlink, and manual-override semantics;
- the PostgreSQL migration operations and legacy backfill;
- the SQLite upgrade path and idempotence behavior;
- all old `JitenCoverUrl` call sites replaced;
- UI provenance labels;
- files added or changed;
- exact focused and full build/test results, including skipped PostgreSQL tests;
- pending-model and `git diff --check` results;
- confirmation that Stage 5 remains default-off;
- confirmation that no provider, scraping, image-download, or Stage 6B work was added.

Do not claim Stage 6A complete if the full suite fails, any existing cover is lost, protected covers can be overwritten, the migration is destructive, or provider work was added.

