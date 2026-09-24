# Wide series, franchise, and anime progress

## Current status

The maintainer accepted [the Batch 0 decisions](wide-series-franchise-anime-decisions.md) and explicitly authorized Batches 1A through 5B plus the book-series milestone review. Batches 1A, 1B, 2A, 2B, 3A, 3B, 4A, 4B, 5A, and 5B are implemented in the working tree on top of `9b822e7`.

The complete book-series milestone through Batch 4B remains accepted in disposable SQLite. Batch 5A added Core-only manual franchise and reviewed Jiten topology contracts, Batch 5B completed the persisted franchise Razor Pages UI, primary navigation, media grouping with separated units, and topology review/receipt workflows. Prompt A added whole-series volume completion/tracking contracts to Core. Prompt B simplified the Series Index and Details UI around the whole-series acceptance example (`1 / 4 completed`, `2 tracked, 2 not tracked`, `37.5%` character progress). Prompt C completed final integration and persisted browser acceptance of that workflow. The full suite passes (771 passed, 0 failed, 6 skipped). All six PostgreSQL integration tests were skipped because `KISEKI_TEST_POSTGRES` is not configured. Anime implementation and further franchise expansion remain deferred.

The shortened runbook in `docs/wide-series-franchise-anime-agent-prompts.md` supersedes the old instruction to proceed to anime research. Prompts A, B, and C are complete. No further feature batch is authorized; anime and further franchise expansion remain deferred.

## Prompt A — whole-series progress contracts

- Extended the immutable `SeriesProgressResult` contract with explicit `ReleasedIncludedInstallmentCount`, `CompletedReleasedIncludedInstallmentCount`, `TrackedReleasedIncludedInstallmentCount`, and `UntrackedReleasedIncludedInstallmentCount` values. These all use the same included-and-confirmed-released scope and count each canonical installment once, regardless of copy count or character metadata.
- Added `IsSeriesComplete`, which is true only when the released/included denominator is nonzero and every installment in it has an explicitly completed copy. The prior `EligibleInstallmentCount` and `UntrackedEligibleInstallmentCount` names remain as read-only compatibility aliases for existing Web and franchise callers.
- Kept `ProgressPercentage` as the existing character-weighted known-data metric. Explicit completion still wins at copy reduction, followed by the persisted TTSU bookmark fraction and then the effective edition character fraction. Catalogue-only volumes remain known zero progress. Existing known-total, computable-progress, and unknown-progress counts/weights expose when that percentage covers only part of the released catalogue; missing totals never remove a volume from completion counts.
- Changed `SeriesCatalogueQueryService.GetDetailsAsync` so its bounded installment list and whole-series summary are separate concerns. The returned row list remains capped at 500, while the summary uses all catalogue installments and scalar copy fields plus grouped activity queries. Index and Details therefore calculate the same whole-series result. All reads remain no-tracking and no session histories are materialized.
- Added focused regressions for the four-volume acceptance case (`1 / 4` completed, `2 / 4` tracked, `37.5%` character progress), multiple copies with one explicit completion, missing canonical totals and unknown copy progress, no eligible volumes, and 501 installments with only 500 Details rows. The long-series fixture asserts that Index and Details agree on counts and percentage.
- No schema, migration, entity, provider-refresh, import, franchise, or Razor presentation change was needed for Prompt A.

### Prompt A verification

- Focused command: `dotnet test Kiseki.Tests/Kiseki.Tests.csproj --no-restore --filter "FullyQualifiedName~SeriesProgressCalculatorTests|FullyQualifiedName~SeriesCatalogueServiceTests"`
- Focused result: 15 passed, 0 failed, 0 skipped.
- Full command: `dotnet test Kiseki.slnx`
- Full result on 2026-09-24: 766 passed, 0 failed, 6 skipped. The same six environment-gated PostgreSQL tests listed below were skipped because `KISEKI_TEST_POSTGRES` is not configured.

## Prompt B — Series UI simplification and acceptance

- Simplified `Kiseki.Web/Pages/Series/Index.cshtml` and `Details.cshtml` around the whole-series acceptance model, leading with volume completion (`1 / 4 completed`), tracking status (`2 tracked, 2 not tracked`), and secondary character progress (`37.5%`):
  - View model extensions in `Kiseki.Web/Models/SeriesViewModels.cs` project Core's `SeriesProgressResult` directly without recalculating formulas: `CompletedVolumesSummary`, `TrackingSummary`, `StatusLabel`, `StatusCss`, `StatusBadgeCss`, and `PartialCoverageLabel` (`covers X of Y volumes`).
  - Tied `ProgressBar.IsComplete` on Index and Details view models strictly to `Progress.IsSeriesComplete` instead of character percentage `>= 100d`, preventing subsets of volumes with 100% reading progress from falsely marking the entire series as complete.
  - Handled empty catalogues and unreleased series honestly: when `ReleasedIncludedInstallmentCount == 0`, views display `"0 released"` and `"No released volumes"` rather than misleading "0 / 0 completed" or false 0% metrics.
- Reorganized Series Details page layout with plain book terminology ("Volumes", "+ Add volume") and compact disclosures:
  - Added a headline `.series-summary-grid` showing Volume Completion, Tracking status, status badge, Reading Progress, and the accessible progress bar.
  - Added a non-headline metadata bar (`.series-non-headline-bar`) showing counts for upcoming, unknown release, and excluded volumes when present.
  - Placed the 7 technical coverage stat boxes into a collapsible `<details class="technical-coverage-details">` disclosure element to reduce visual noise while keeping granular debugging metrics accessible.
  - Tucked raw order keys (`#100`) and ordering buttons into the compact copy management toolbar at the bottom of each installment card.
  - Kept all existing functionality accessible: Create/Edit series, + Add volume, Jiten refresh, + Track copy, Edit installment, Consolidate, and copy management.
- Highlighted unknown release states with a prominent warning notice linking directly to `#first-unknown-volume`, and added direct "Edit volume" buttons on unknown-release volume cards (`#editInstallment-@inst.Id`) so imported provider entries never silently disappear from the user's awareness.
- Added responsive styling in `Kiseki.Web/wwwroot/css/site.css` ensuring summary grid collapses gracefully to single-column on mobile viewports (`max-width: 768px`).

### Prompt B verification

- Focused unit & render tests in `Kiseki.Tests/SeriesPageTests.cs`:
  - `FourVolumeReferenceCase_DetailsAndIndex_ShowsExpectedCompletionTrackingAndProgress`: verifies the exact reference acceptance case (`1 / 4 completed`, `2 tracked, 2 not tracked`, `37.5%` progress, "In progress" badge).
  - `FourVolumeReferenceCase_WithUnknownTotal_ShowsPartialCoverageAndMaintainsCompletion`: verifies that missing volume character counts produce partial coverage labels (`covers 3 of 4 volumes`) without corrupting volume completion (`1 / 4`).
  - `SubsetWith100PercentCharacterProgress_DoesNotMarkWholeSeriesComplete`: verifies that 100% character progress on 2 out of 3 volumes does not mark the whole series complete.
  - `EmptyCatalogueAndNoReleasedVolumes_HandledHonestlyWithoutFalseZeroCompletion`: verifies honest empty states when no volumes exist or none are released.
- Command: `dotnet test Kiseki.Tests/Kiseki.Tests.csproj --no-restore --filter "FullyQualifiedName~SeriesPageTests"` (23 passed, 0 failed).
- Automated browser verification on disposable SQLite (`artifacts/merge-validation/browser/run_prompt_b_browser_verification.cjs`):
  - Spun up Kestrel with disposable SQLite database seeded with the four-volume reference acceptance dataset.
  - Verified desktop viewport (1440x1000): confirmed rendered values on Series Index and Details, interactive `+ Add volume` toggle, `+ Track copy` button on catalogue volume, and `<details>` coverage disclosure expansion.
  - Verified mobile viewport (390x844): confirmed responsive single-column layout with zero horizontal overflow (`scrollWidth <= clientWidth`).
  - Monitored page errors and console errors: 0 errors detected.
- Full command: `dotnet test Kiseki.slnx`
- Full result on 2026-09-24: 770 passed, 0 failed, 6 skipped (the 6 environment-gated PostgreSQL tests were skipped as expected). Prompt C subsequently completed the final integration review recorded below.

## Batch 1A implementation

- Added `MediaInstallment` with nullable series membership, media type, explicit order, kind, release state/date, inclusion, separate legacy/canonical/manual title fields, canonical count/cover provenance, and concurrency version.
- Added provider identities keyed by `(Provider, NormalizedKey)` and separate immutable provider snapshots keyed by identity and fingerprint. Database checks validate IDs, keys, enum ranges, totals, order, and cover/source consistency.
- Added nullable `MediaWork.MediaInstallmentId` with restrictive installment deletion and retained `MediaWork.MediaSeriesId` plus all legacy Jiten, count, cover, TTSU, log, binding, and receipt fields.
- Generated PostgreSQL migration `20260921172122_AddCanonicalInstallments` with the repository-local `dotnet-ef` tool and updated its designer/model snapshot.
- PostgreSQL backfill takes a transaction-scoped advisory lock. SQLite startup takes an immediate write transaction before `EnsureCreated`/upgrade work. Both use deterministic installment IDs and idempotent inserts.
- Backfill creates one installment per work, preserves standalone membership, seeds order keys 100 apart, and initializes work/installment versions from the work ID.
- Unique legacy Jiten claims create `deck:{id}` or `subdeck:{parent}:{child}` identities and incomplete legacy snapshots. Duplicate claims create no identity or canonical provider metadata and remain reviewable through unchanged work fields.
- Only positive unique Jiten counts become canonical denominators. Existing copy fields and explicit zero overrides are unchanged. Only Jiten-specific/fallback copy covers seed canonical cover provenance; every copy cover remains unchanged.
- Extended `SqliteSchemaUpgrade` without recreating databases or applying PostgreSQL migrations.

## Batch 1B implementation

- Added `MediaCatalogService` as the Core compatibility boundary for tracked-copy creation, installment assignment, series moves, type changes, and Jiten provider linking/unlinking. Its detached creation/link helpers give non-persistent convenience import paths the same complete copy/installment graph before a `DbContext` is available.
- New persistent copies from Console Jiten add and `TtsuImportService` now receive an installment. The TTSU convenience importer and legacy in-memory aggregator also create an installment; the latter records Jiten canonical identity/snapshot evidence in its detached graph.
- Console media-type changes detach the copy to a compatible standalone installment and remove an orphaned Jiten identity/link first. Console series assignment moves the canonical installment and synchronizes `MediaSeriesId` for every linked copy. Console Jiten link/unlink now use the Core provider command.
- Web Jiten POST still refetches and validates the Jiten selection, then uses the Core provider command. Relinking removes an unreferenced prior identity; unlinking removes an identity only when another copy on the installment does not retain that claim. Protected copy covers remain protected by the existing `MediaWork` domain methods.
- TTSU previews, tracked imports, Library index/details, and Console library reads explicitly load installment and canonical series data alongside the pre-existing logs/legacy series paths. Library display prefers canonical series membership with a legacy fallback during compatibility.
- Added `MediaCatalogServiceTests` for copy creation, multi-copy legacy-series synchronization, Jiten relink/unlink identity cleanup with protected covers, and media-type detachment. The TTSU convenience importer regression now asserts its installment graph.

### Remaining compatibility limitations

- Legacy `MediaSeriesId`, legacy Jiten fields, and copy metadata remain stored and are intentionally retained until a later cleanup migration. Unsupported old binaries can still bypass the Core command boundary and are not safe schema writers.
- A Jiten key already claimed by another canonical installment is rejected for review/consolidation; Batch 1B does not merge copies, histories, titles, or provider claims.
- Catalogue refresh/reconciliation, canonical metadata resolution for all read models, and a manual installment UI remain later batches. This batch keeps the existing Library/Console behavior rather than exposing a catalogue UI.

## Batch 2A implementation

- Added the pure `SeriesProgressCalculator` with immutable `SeriesProgressInput`, `InstallmentProgressInput`, `CopyProgressInput`, `InstallmentProgressResult`, and `SeriesProgressResult` records. It first reduces copies to one installment fraction (explicit completion wins; otherwise greatest known edition fraction) and then calculates weighted progress once per released, included installment.
- Canonical totals are the weighting denominator. Catalogue-only released entries are known zero-progress; tracked entries with an unknown copy fraction are excluded from the computable denominator and reported as unknown-progress coverage; no computable weight returns `null`, never a false 0%. Lifetime characters and minutes remain independent sums of actual copy activity.
- Added transactional `SeriesCatalogueService` commands: `CreateSeriesAsync`, `EditSeriesAsync`, `AddInstallmentAsync`, `EditInstallmentAsync`, `ReorderInstallmentsAsync`, `MoveInstallmentAsync`, `CreateCopyAsync`, `AssociateCopyAsync`, and `ConsolidateInstallmentsAsync`. Manual changes require current installment/copy versions where they alter, associate, reorder, or consolidate catalogue state. Consolidation is an explicit reviewed copy move: it preserves every copy ID, log, binding, title, cover, provider identity, and source installment rather than merging/deleting history.
- Added bounded no-tracking `SeriesCatalogueQueryService` records for index/details. It reads only scalar work fields, grouped log totals, TTSU bookmark fractions, and relevant provider keys; it never materializes session histories for a series card. It applies copy count precedence as manual (including explicit zero as unknown), TTSU, exact compatible canonical Jiten identity, then legacy Jiten.
- The Series query output includes released/included, untracked, excluded, upcoming, unknown-release, canonical-total, computable-progress, unknown-progress, and lifetime-activity coverage for a real persisted UI workflow. Catalogue-only installments produce no `MediaWork`, so the existing Library remains copy-only.
- Added calculator and SQLite-backed command/query fixtures for zero, one, and multiple copies; manual completion without an edition total; different edition lengths; explicit-zero unknown progress; unknown release/upcoming/excluded entries; canonical metadata coverage; ordering; association; and lossless reviewed consolidation.

## Batch 2B implementation

- Replaced placeholders across `Kiseki.Web/Pages/Series` with real persisted Index, Details, Create, and Edit workflows and manual installment controls.
- Implemented immutable view models in `Kiseki.Web/Models/SeriesViewModels.cs` (`SeriesIndexItemViewModel`, `SeriesDetailsViewModel`, `SeriesInstallmentViewModel`, `SeriesCopyViewModel`, `AvailableCopyOption`).
- Connected Index to `SeriesCatalogueQueryService.GetIndexAsync`:
  - Filter tabs for All, Books, Anime, Games via `MediaType?`.
  - Display truncation status, accessible progress bars, volume/installment counts, untracked counts, and lifetime activity.
  - Retained empty-state navigation to series creation.
- Connected Create to `SeriesCatalogueService.CreateSeriesAsync`:
  - Validates required trimmed title and media type.
  - Redirects to Series details with PRG `TempData["LibraryNotice"]`.
- Connected Edit to `SeriesCatalogueService.EditSeriesAsync`:
  - Validates required trimmed title and presents media type as non-editable.
  - Handles missing series (404) and conflict exceptions with friendly notices.
- Connected Details to `SeriesCatalogueQueryService.GetDetailsAsync` and `SeriesCatalogueService` commands:
  - Renders headline progress percentage (`16.7%` for North Wind Novels) and accessible progress bar.
  - Renders coverage metrics: eligible/released, untracked, known canonical totals, unknown canonical totals, computable progress, unknown progress, upcoming, unknown release, excluded, lifetime characters/minutes.
  - Shows ordered installment cards with order key, kind, title, status badges (Released/Upcoming/Unknown, Included/Excluded, Tracked/Catalogue-only, Completed), canonical character counts, and release dates.
  - Displays attached tracked copies (with links to `/Library/Details`, reading status, character/minute totals, and bookmark positions).
  - Renders cover frames with fallback glyphs (`本`, `アニメ`, `ゲーム`) using `data-cover-frame` and `data-cover-fallback`.
  - Details PRG form handlers with antiforgery:
    - `OnPostAddInstallmentAsync`: adds installment with kind, release state, release date, canonical count, order key, and inclusion.
    - `OnPostEditInstallmentAsync`: edits installment metadata with concurrency token validation (`ExpectedVersion`).
    - `OnPostReorderInstallmentsAsync`: moves installments up or down, or updates ordered sequence.
    - `OnPostCreateCopyAsync`: tracks an installment by creating a linked `MediaWork` copy (appearing in both Series details and Library).
    - `OnPostAssociateCopyAsync`: links an existing library copy to an installment, preserving logs and IDs.
    - `OnPostConsolidateAsync`: moves copies from a duplicate installment into a target canonical installment with concurrency verification (`SourceExpectedVersion`, `TargetExpectedVersion`), preserving copies, logs, and source installments.
- Handled empty, invalid ID, 404, and concurrency conflict states (`MediaCatalogConflictException`) with friendly messages and retry actions.
- Scoped dark-mode styles added to `Kiseki.Web/wwwroot/css/site.css` for coverage stat cards, installment cards, and notice banners.

### Changed files in Batch 2B

- `Kiseki.Web/Models/SeriesViewModels.cs` (new)
- `Kiseki.Web/Pages/Series/Index.cshtml` & `Index.cshtml.cs`
- `Kiseki.Web/Pages/Series/Create.cshtml` & `Create.cshtml.cs`
- `Kiseki.Web/Pages/Series/Edit.cshtml` & `Edit.cshtml.cs`
- `Kiseki.Web/Pages/Series/Details.cshtml` & `Details.cshtml.cs`
- `Kiseki.Web/wwwroot/css/site.css`
- `Kiseki.Tests/SeriesPageTests.cs` (new)
- `docs/wide-series-franchise-anime-progress.md`

### Batch 2B integration review and fixes

- The work was already present in the dirty `main` checkout rather than on a separate branch. The actual diff and Batch 2A handoff were reviewed; unrelated changes and the user-owned untracked prompt were preserved.
- All installment/copy mutations now carry the route series ID into Core. Core proves target ownership, rejects cross-series IDs and invalid enum values, and maps EF concurrency failures to retryable `MediaCatalogConflictException` messages.
- Relative up/down ordering is owned by `SeriesCatalogueService.MoveInstallmentAsync`, not a PageModel. Both relative and complete-list reorders require an exact ID/version snapshot so a concurrent add, edit, or reorder cannot be overwritten.
- Direct association is limited to a versioned unassigned copy or a reviewed standalone installment. Copies already belonging to a series require reviewed consolidation. A valid standalone association preserves the copy ID, title, logs, binding, cover, source installment, and compatible legacy series state; matching Jiten identity and snapshots move to the target only when no retained source copy still claims them.
- Reviewed consolidation verifies route ownership plus source/target versions. It preserves the source installment and copy histories, moves matching Jiten identity ownership with the selected copies, and rejects a partial move that would leave a provider identity stale.
- `SeriesCatalogueQueryService` now projects immutable `CatalogueCover` values for series, installment, and copy results with explicit canonical exact/parent/legacy and copy-local origins. The UI uses the existing lazy, no-referrer cover/fallback behavior. Reads remain bounded and no-tracking; the available-copy query includes only same-type unassigned or standalone copies.
- POST forms retain antiforgery tokens and PRG. Posted review/version values are never authoritative by themselves: Core reloads and validates series, installment, copy, relationship, media type, identity, and version state before saving.
- Series controls now have associated labels and collapse state/ownership attributes. Dark-theme contrast was corrected after an automated WCAG audit found Bootstrap light-theme utility colors overriding the Series cards. The add form now defaults visibly and semantically to `Volume` and `Released`.
- Added regressions for route tampering, invalid enums, stale reorder snapshots, safe standalone association, provider identity movement, available-copy filtering, cover provenance, lossless consolidation, and query tracking behavior.

## Batch 3A implementation

- The recorded Series UI review was treated as the prerequisite gate. Batch 2B remains complete; no Series UI files were changed for this feature batch.
- Fixed a prerequisite metadata-ownership defect before refresh work: manual Series character-count edits now persist in `MediaInstallment.CharacterCountOverride` and effective reads prefer it over provider-owned `CanonicalCharacterCount`. The additive PostgreSQL migration and SQLite upgrade preserve existing canonical data and default the new override to null.
- Added completeness-aware `IJitenApiClient.GetDeckCatalogueAsync` / `JitenDeckCatalogueFetchResult`. `JitenApiClient` retains the existing detail pagination and Jiten cover selection/fallback pipeline, caps refreshes at 1,000 items/100 pages, detects stalled/duplicate/inconsistent pagination, propagates cancellation, and never reports a partial fetch as evidence of absence.
- Added the pure `JitenCatalogueReconciliationPlanner` and immutable provider/local/proposal/choice/review records. It reports exact identities, additions, legacy-claim/title associations, duplicate provider identities, manual conflicts, uncertain order/editions, complete-fetch disappearance, and incomplete-fetch absence that cannot be acted on.
- Added `JitenCatalogueReconciliationService` with `PreviewAsync`, `Approve`, `ApplyAsync`, and `GetReceiptAsync`. Network fetch and cover normalization happen before the serializable transaction. Preview state is held by the bounded, expiring singleton `IJitenCatalogueReviewStore`; approval retains validated choices server-side. Apply re-fetches Jiten, compares provider and bounded local fingerprints/versions, reloads tracked state, and atomically applies only the approved actions.
- Added `InstallmentProviderIdentity.LastSeenAtUtc` / `MissingSinceUtc` and durable `JitenCatalogueRefreshReceipt`. Complete disappearance only marks an identity missing. Refresh never deletes an installment, copy, history, or snapshot and never creates a `MediaWork`. A different existing series anchor must be changed explicitly rather than silently re-associated.
- Canonical provider title/count/release/cover fields and immutable snapshots update without touching title/count/release overrides, stored order/inclusion, or any copy metadata. Invalid child covers use the existing validated parent fallback (or no cover) and do not invalidate catalogue metadata.
- Operation receipts are checked before review-state access, so a retry after a lost response succeeds even after the in-memory review has expired or the process has restarted. Provider identity/snapshot keys, serializable apply, collision mapping, and local fingerprints prevent duplicate concurrent hydration; a losing distinct operation must review again.
- Generated PostgreSQL migration `AddJitenCatalogueRefreshState` with the local `dotnet-ef` tool and updated the model snapshot. `SqliteSchemaUpgrade` adds the manual count override, identity observation fields, receipt table, and index transactionally without recreating an existing database.

### Batch 3B handoff

Use the persisted Core workflow; mock-only rendering is not acceptance evidence.

**Core contracts (do not duplicate in Web):**

- `JitenCatalogueReconciliationService.PreviewAsync(Guid seriesId, int jitenDeckId, CancellationToken)` returns `JitenCatalogueReview`.
- `JitenCatalogueReconciliationService.Approve(Guid reviewId, string expectedFingerprint, IReadOnlyList<JitenCatalogueChoice>)` validates every choice against the server-held review and returns the review with `ApprovedChoices` and `ApprovalFingerprint`.
- `JitenCatalogueReconciliationService.ApplyAsync(Guid operationId, Guid mediaSeriesId, Guid reviewId, string approvalFingerprint, CancellationToken)` revalidates provider/local evidence and returns immutable `JitenCatalogueRefreshReceiptResult`; `GetReceiptAsync(Guid operationId, Guid mediaSeriesId, CancellationToken)` supports ownership-scoped receipt display/recovery.
- Proposal kinds: `ExactIdentity`, `Addition`, `AmbiguousAssociation`, `DuplicateProviderIdentity`, `MissingProviderEntry`, `AbsenceUnverified`. Actions: `Apply`, `AddNew`, `LinkExisting`, `MarkMissing`, `Ignore`. Render only `AllowedActions`; submit proposal ID/action and an installment ID only for `LinkExisting`. Titles, counts, covers, provider IDs, versions, allowed actions, and target candidates are never POST authority.
- `JitenCatalogueReviewRequiredException` is the expected stale/expired/race/domain response; `CanRetryCurrentReview` distinguishes correctable submitted choices from changed evidence, and `GetActiveReview` exposes current server-held state without leaking the review store to Web. `JitenHttpException` retains status/retry information for actionable fetch errors. Cancellation must flow from every handler.
- DI is ready: `IJitenCatalogueReviewStore` is singleton and `JitenCatalogueReconciliationService` scoped. Review lifetime is 20 minutes and the in-memory store is bounded to 256 reviews; process restart requires a new preview unless a durable operation receipt already exists.

**Routes and handler expectations:**

- Add a visible refresh action to `/Series/Details?id={seriesId}` and implement a dedicated authenticated Razor Page at `/Series/RefreshJiten?seriesId={seriesId}` (recommended files `Pages/Series/RefreshJiten.cshtml` and `.cshtml.cs`). Keep the manual Details editor unchanged except for the navigation/action and optional last-result notice.
- GET loads the real series details and current anchor. An unanchored book series accepts a positive deck ID; an anchored series refreshes that exact deck. Do not offer silent anchor replacement. Non-book, missing, or invalid targets return friendly 404/validation states.
- `OnPostPreviewAsync` calls `PreviewAsync`; `OnPostApplyAsync` first calls `Approve` with the posted proposal choices, then calls `ApplyAsync` with a server-generated operation ID that is retained in the confirmation form for browser retry. Use antiforgery and PRG after success. Hidden review/fingerprint/operation values are correlation tokens, not metadata authority.
- Catch review-required, cancellation, Jiten HTTP/rate-limit, and unexpected failures separately. Preserve the review screen for validation errors; stale/expired/provider-changed/local-changed/race responses must instruct the user to fetch a new preview.

**Deterministic fixtures and acceptance cases:**

1. `North Wind` / deck 10: exact `subdeck:10:101` changes provider title/count/cover while manual title, 81,234 count override, release correction, order, inclusion, and protected TTSU copy cover remain; new `subdeck:10:102` adds one catalogue-only installment and zero copies.
2. Two local `Same Volume` installments retain duplicate legacy `subdeck:50:501` copy claims. The review shows both candidates and requires explicit association; it never merges copies or histories. Two provider entries with the same title show uncertain edition/order state.
3. A duplicate provider key is non-applicable except `Ignore`. A complete fetch missing `subdeck:40:402` offers `MarkMissing`/`Ignore`; the same payload marked incomplete shows `AbsenceUnverified` and only `Ignore`.
4. Change either an installment version/manual correction or a stubbed Jiten title/count between preview and apply. The page shows `review again`, and no mutation/receipt is written.
5. Apply an approved addition twice with the same operation ID (including after replacing the review store): show the committed receipt and keep one installment/identity/snapshot. Two concurrent distinct reviews produce one hydration winner and one review-required result, never duplicates.

**Required UI states:** loading/fetching, empty/no changes, review with completeness and expiry, exact update, manual-conflict protection, ambiguous association, duplicate identity, uncertain order, complete disappearance, unverified absence, applying/disabled controls, success receipt, replayed receipt, expired/stale review, cancellation, not found, rate limited/retry-after, incomplete pagination warning, and generic non-destructive error. Use associated labels/fieldsets, keyboard-operable controls, an accessible error summary/live status, visible focus, semantic status text (not color alone), and the existing lazy/no-referrer cover fallback behavior.

**Allowed Batch 3B files:** new `Kiseki.Web/Pages/Series/RefreshJiten.cshtml(.cs)`, new immutable Web view models under `Kiseki.Web/Models`, the refresh link/notice only in `Pages/Series/Details.cshtml(.cs)`, scoped additions to `wwwroot/css/site.css` and `wwwroot/js/site.js`, and focused Web tests such as `Kiseki.Tests/SeriesJitenRefreshPageTests.cs`. Do not modify Core entities/services, migrations/snapshot, Jiten client semantics, TTSU import, Console, progress policy, or existing manual command rules; report a Core contract gap instead.

Batch 3B exit requires a real browser-to-SQLite preview/choice/apply/replay flow using stubbed HTTP provider evidence, restart-backed receipt display, mobile layout/keyboard checks, no console errors, and an automated accessibility pass. It must prove the new catalogue entry is visible in Series and absent from Library.

## Audit findings

### Import review and transactions

- `TtsuMergePlanner` creates immutable daily/bookmark decisions and fingerprints target/source/binding/log/progress/total/completion evidence.
- Web review tokens bind the server-held book, fingerprint, metadata candidate, and cover choice. Confirmation rejects unknown/cross-book tokens and refetches selected Jiten metadata.
- `TtsuImportService.ApplyAsync` recomputes preview inside a serializable transaction. Target, orphan, source-match, binding, log, or reviewed-choice changes require review again.
- Duplicate stored days, conflicting revisions, and orphan assignment require explicit choices. Missing dates and non-TTSU activity are retained.
- The actual Web batch lifetime is 20 minutes sliding with a two-hour absolute cap, not the 30 minutes described in `AGENTS.md`. Expiration requires re-upload; this documentation drift is not a schema blocker.

### Receipts, retries, and collisions

- The server-generated Web batch ID is the durable operation ID.
- Receipt lookup occurs before a new transaction and again after a recognized uniqueness/serialization/locking collision. A lost response can return the committed receipt without replaying writes.
- Concurrent first imports are protected by recomputing source matching during apply; changed match evidence invalidates the fingerprint.
- Receipt idempotency is intentionally operation-ID based, not payload-hash based. A batch ID must never be reused for another logical request.
- PostgreSQL tests exist for upgrade preservation, concurrent update review, lost-response replay, and concurrent first import, but were skipped because `KISEKI_TEST_POSTGRES` was not configured.

### Cover and provider trust boundaries

- `LegacyUnknown`, `UserOverride`, and `Ttsu` cover sources are protected. Jiten application also refuses to replace Google Books/Open Library selections.
- Web Jiten POST refetches detail through `JitenSelectionResolver`; hidden titles, counts, covers, and child relationships are not trusted.
- External cover methods validate HTTPS, allowed hosts, URL-safe provider IDs, and lengths. TTSU cover import uses its dedicated local/HTTPS path.
- TTSU folder covers may replace non-protected provider covers under current policy. Invalid optional covers do not abort reading-history import.
- Migration must preserve the exact current copy cover/source/provider ID; canonical refresh must not silently rewrite it.

### Schema upgrades

- PostgreSQL uses ordered EF migrations through `AddJitenCatalogueRefreshState`; the snapshot includes canonical installments, separate manual count corrections, provider observation state, and durable Jiten refresh receipts in addition to import/cover state.
- Existing SQLite files use transactional `SqliteSchemaUpgrade` after `EnsureCreatedAsync`. It creates missing import tables, inspects columns before additive alters, migrates legacy cover naming/provenance, and creates the unique bound-day index.
- Existing SQLite upgrade/repeat/preservation tests pass. PostgreSQL execution remains unproven in this environment and is a hard Batch 1A release gate.

### Writers and queries

The audit found direct relationship/identity writers in persistent TTSU import, TTSU convenience import, the legacy aggregator, Console Jiten creation, Console type/series/Jiten editing, Web Jiten linking, and Web detail scalar handlers. Library, Details, Dashboard, Console, and TTSU preview queries were also inventoried. The complete table and required command boundaries are in [the decisions document](wide-series-franchise-anime-decisions.md); contract drafts are in the [Batch 0 audit](wide-series-franchise-anime-batch-0.md).

The direct public setters are not an immediate data-loss bug, but they are a blocking architectural bypass for Batch 1B: all supported writers must move behind Core commands before legacy relationships can be retired.

## History and missing evidence

Available commits for title parsing/matching, metadata receipts, Google Books/Open Library covers, TTSU folder covers, and their tests were reviewed through `8a12cb1`. Current code and tests, not prompt claims, are the evidence used here.

The proposal references `automatic-import-implementation-plan.md` and Stage 6A/6B audit/repair artifacts that are not present in this checkout. They were not reconstructed or assumed complete. `AUTOMATIC_IMPORT_DESIGN.md` exists at repository root as background design, and `docs/antigravity-serpapi-amazon-cover-prompt.md` is a prompt rather than completion evidence. Optional cover-provider expansion is not part of this feature.

## Batch 3B implementation

- Built the dedicated authenticated Series Jiten refresh and review workflow at `Pages/Series/RefreshJiten.cshtml` and `RefreshJiten.cshtml.cs`.
- Implemented immutable view models in `Kiseki.Web/Models/SeriesRefreshJitenViewModels.cs` (`SeriesRefreshJitenViewModel`, `JitenReviewProposalViewModel`, `CandidateInstallmentOption`, `JitenRefreshReceiptViewModel`, `ProposalChoiceInput`).
- Added visible "Refresh from Jiten" button to `Pages/Series/Details.cshtml` in the header actions when `series.MediaType == MediaType.Book`.
- Page flow and handlers:
  - `OnGetAsync`: Loads series, checks for book media type (friendly rejection for non-book series). If `operationId` is specified, loads the durable receipt and renders receipt view (with replayed notice if applicable). If unanchored, renders deck prompt; if anchored, presents direct "Fetch Jiten Preview" action.
  - `OnPostPreviewAsync`: Enforces anchored deck ID or validates positive deck ID for unanchored series. Calls `_reconciliationService.PreviewAsync`, loads local candidate installment titles, constructs review proposals with semantic badges (`Exact Match`, `New Volume`, `Ambiguous Association`, `Duplicate Identity`, `Disappeared From Jiten`, `Absence Unverified`), and displays expiry timer, incomplete fetch warning, and manual conflict callout. Retains a generated `operationId` in the form.
  - `OnPostApplyAsync`: Converts user selections to domain choices, calls `_reconciliationService.Approve`, then calls `_reconciliationService.ApplyAsync`. Uses Post/Redirect/Get to redirect to receipt state.
  - Error and stale review handling: Catches `JitenCatalogueReviewRequiredException` (renders friendly error and "Fetch New Preview" button when stale/expired), `JitenHttpException` (renders rate limit and `RetryAfter` countdown information), and cancellation.
- Added scoped dark-mode styling to `Kiseki.Web/wwwroot/css/site.css` for deck prompt, review header, proposal cards, badges, conflict callouts, actions fieldsets, candidate dropdowns, and receipt stat cards.
- Added comprehensive unit and PageModel tests in `Kiseki.Tests/SeriesJitenRefreshPageTests.cs`:
  - `NorthWind_PreviewAndApply_HydratesCatalogueWithoutCreatingCopies_AndPreservesManualAndCopyMetadata`
  - `AmbiguousAssociation_ShowsCandidates_AndAllowsExplicitLink`
  - `DuplicateProviderIdentity_And_MissingEntries_FollowAllowedActions`
  - `StaleReview_LocalChangeOrVersionChange_ShowsReviewAgain`
  - `IdempotentReplay_SameOperationId_ReturnsCommittedReceipt`
  - `HttpError_And_CancellationHandling`
  - `UnanchoredSeries_RequiresPositiveDeckId_AndNonBookRejected`
  - `ReceiptDisplay_DoesNotCrossSeriesBoundary`
  - `MissingOperationCorrelation_PreservesTheActiveReviewForCorrection`

### Batch 3B integration review and fixes

- The Gemini work was present directly in the dirty `main` checkout, not on a separate branch. The actual files and the Batch 3A handoff were reviewed; unrelated changes, including the deleted cover prompt and the untracked agent-prompts document, were not reset or discarded.
- Receipt lookup and lost-response replay are now series-scoped in Core. `GetReceiptAsync(Guid operationId, Guid mediaSeriesId, ...)` and `ApplyAsync(Guid operationId, Guid mediaSeriesId, ...)` prevent a receipt or operation ID from another series from being displayed or replayed. Apply also proves that the server-held approved review belongs to the requested series.
- `JitenCatalogueReviewRequiredException.CanRetryCurrentReview` and `JitenCatalogueReconciliationService.GetActiveReview` now distinguish correctable submitted-choice/correlation errors from stale evidence. The PageModel no longer parses exception text, accesses the review store directly, or uses `DateTimeOffset.UtcNow` instead of the Core clock.
- Candidate title loading is no-tracking and limited to the reviewed candidate/existing installment IDs. Unexpected errors are logged and mapped to non-destructive user messages rather than exposing exception details. A missing receipt for a supplied operation ID produces an error instead of silently returning to the deck prompt.
- Empty/no-change reviews can be confirmed, and preview/apply forms expose accessible live loading text and disable their submit button after submission. The reviewed operation ID is required rather than silently replaced on a malformed confirmation. Razor form tag helpers provide antiforgery tokens, and browser POSTs confirmed those tokens are accepted.
- The local Jiten HTTP base can be overridden through `Jiten:BaseAddress`; only HTTPS or loopback HTTP is accepted. Production still defaults to `https://api.jiten.moe/`. The earlier cover-read-model and `CanonicalCoverSource.None` notes were not contract gaps: immutable catalogue cover records already contain URLs/provenance, and `None` correctly represents the absence of a canonical cover.
- Gemini's xUnit browser method was removed because it silently returned when one hard-coded Chrome path was unavailable and depended on an ignored script. Browser acceptance is an explicit environment check, separate from the deterministic unit/PageModel suite.
- Restart testing found and fixed a prerequisite SQLite correctness defect. The repeated legacy backfill used every canonical Jiten identity as if its installment ID were a legacy work ID; hydrated installments could therefore receive a null `CanonicalCoverSource` and prevent startup. The backfill is now limited to identities with the legacy same-ID work, with `RepeatedStartup_PreservesHydratedInstallmentsWhoseIdsAreNotLegacyWorkIds` covering the failure.
- Automated axe scans initially found dark-theme contrast failures from Bootstrap utility colors and the primary button. Scoped review/receipt overrides and primary-button foreground variables fixed them.

### Batch 3B browser-to-persistence verification

- Ran a live Development Kestrel process against a disposable SQLite file and a local HTTP Jiten fixture through `Jiten:BaseAddress`, using the required `agent-browser` workflow.
- Created the `North Wind` series through the browser, entered deck 10, submitted preview by keyboard, reviewed two provider-backed additions, applied them, and observed a durable receipt reporting 2 added installments, 2 identities, and 2 metadata updates.
- Returned to Series Details, created one tracked copy for Volume 1, and verified Library contained Volume 1 but not catalogue-only Volume 2.
- Stopped and restarted the Web process against the same SQLite file, thereby replacing the in-memory review store. The same operation receipt rendered as `Replayed Result` with the original counts and timestamp.
- Review and restarted-receipt axe 4.12.1 WCAG 2 A/AA audits both completed with 0 violations and 0 incomplete checks after fixes. Keyboard submission worked, the 390x844 viewport had no horizontal overflow, and browser console/page error checks were empty.
- The disposable SQLite file and local provider stub were removed after verification. PostgreSQL was not used for this browser flow.

## Batch 4A implementation

- Confirmed Batch 3B is complete in the current checkout before starting this batch. Its Core receipt/review ownership fixes, accessible Series refresh UI, restart-backed browser flow, and 730-pass/6-skip verification remain present; no prerequisite 3B repair was required.
- Added the three explicit Core target intents in `TtsuCopyIntent`: `ExistingCopy`, `NewCopyUnderExistingInstallment`, and `NewInstallmentAndCopy`. `TtsuImportTargetChoice` carries only the ID valid for the selected intent.
- Added immutable target-review records: `TtsuImportTargetReview`, `TtsuCopyTargetCandidate`, `TtsuInstallmentTargetCandidate`, `TtsuProviderIdentityHint`, and `TtsuInstallmentTarget`. Copy candidates expose work/installment versions, assignment, binding state/version, and lifetime activity; installment candidates expose version, series assignment, copy/unbound-copy counts, and provider keys.
- `TtsuImportService.ReviewTargetAsync` now applies match precedence in this order: persisted normalized TTSU title/folder hints; a unique canonical provider identity; exact legacy copy provider claims (duplicate claims remain explicit ambiguity); unique normalized title/copy or catalogue-title suggestions; then a new standalone installment and copy.
- A bound copy is reusable only for the same persisted TTSU source. A provider-matched copy already bound to another source produces a new-copy-under-installment suggestion, preserving both histories. Multiple unbound/provider/title candidates never receive a silent winner.
- `PreviewTargetAsync` extends the existing day/bookmark planner. Its fingerprint includes the chosen intent and IDs, target work/installment versions and assignment, selected-installment state, binding identity/version, source-match candidates, persisted provider evidence, orphan assignments, daily conflict choices, bookmark choice, logs, totals, and completion state. The legacy `PreviewAsync` contract remains as a compatibility wrapper.
- `TtsuImportRequest` now carries the reviewed target choice and persisted provider identity. `ApplyAsync` re-runs `PreviewTargetAsync` inside the existing serializable execution-strategy transaction before any mutation. Changed installment versions, moved copies, competing bindings, changed exact identities, or changed merge choices require a fresh review.
- The same transaction now selects or creates the catalogue installment, creates or reuses the copy, applies authoritative Jiten metadata when available, writes logs/bookmark/TTSU total/binding, and inserts the durable receipt. New-copy creation uses `MediaCatalogService.CreateTrackedCopyAsync`; no direct relationship writer was added. Provider uniqueness/serialization/locking collisions still roll back and return either the committed operation receipt or a review-required result.
- TTSU bookmark state and inferred edition total remain on the selected/new `MediaWork` and its `TtsuBinding`. No copy histories, titles, IDs, or logs are merged. Folder covers and optional enrichment retain their prior nonfatal/protection behavior.
- Added PageModel plumbing without implementing the Batch 4B controls: `TtsuBookSelectionInput.CopyIntent`/`InstallmentId`, `TtsuModel.InstallmentTargets`, server-held target choice/provider evidence in `TtsuReviewedPlan`, target-aware preview population, and confirmation validation against that review. The existing legacy Create/Merge form remains compatible until 4B: a server-reviewed new-copy-under-installment choice survives review/confirm even though the compact controls are not rendered yet.
- Batch expiry still requires re-upload/review. Receipt lookup still occurs before batch lookup, so a lost-response replay succeeds after the in-memory batch is gone without resolving provider data or writing a second copy.

### Batch 4A regression evidence

- `TtsuInstallmentImportTests` covers catalogue-only exact-provider import without a duplicate installment, operation replay, copy-local TTSU totals/bookmark/binding, reread creation under the same installment with independent logs, ambiguous normalized titles, duplicate legacy provider claims, changed installment versions, a competing binding, and duplicate-source rejection within one batch.
- `TtsuImportPageTests.Confirm_ExactCatalogueIdentity_CreatesCopyUnderExistingInstallment` exercises upload -> enrichment -> server-held target review -> confirmation -> persisted linked copy through the real PageModel and Core transaction.
- Existing TTSU expiration, cross-book token rejection, changed-target re-review, source conflict, orphan assignment, cover protection, resolver-failure, atomic rollback, receipt replay, and PostgreSQL concurrency fixtures continue to run unchanged.
- No entity/schema change was required. PostgreSQL was not executed for this batch; its six existing environment-gated tests remain skipped because `KISEKI_TEST_POSTGRES` is not configured.

### Batch 4B handoff

**Stable backend/PageModel contracts:**

- Read `TtsuBookPreviewViewModel.Plan.TargetReview` for `Reason`, `IsAmbiguous`, `SuggestedChoice`, `CopyCandidates`, and `InstallmentCandidates`.
- Post `Selections[i].CopyIntent` plus exactly one of `TargetId` or `InstallmentId` as described by `TtsuImportTargetChoice`; retain `BookKey`, `Selected`, `ReviewToken`, day/orphan/progress choices, `CandidateKey`, and `SelectedCoverKey`. Changing a target must submit the existing `Review` handler before confirmation so a new server-held fingerprint/token is rendered.
- `TtsuModel.Targets` supplies all book copies for an explicit existing-copy selector. `TtsuModel.InstallmentTargets` supplies all book installments for an explicit new-copy-under-installment selector. Candidate records provide the binding/history/copy-count context needed to label risky choices without querying in Razor.
- `OnPostPreviewAsync`, `OnPostEnrichNextAsync`, `OnPostReviewAsync`, and `OnPostConfirmAsync` already own parsing, enrichment, authoritative review, stale validation, re-fetch, atomic persistence, and PRG. UI code must not reproduce matching rules or trust posted titles/provider metadata.

**File ownership and allowed 4B edits:**

- Codex-owned and complete for this handoff; do not edit without reporting a contract gap: `Kiseki.Core/Models/TtsuImportPlan.cs`, `Kiseki.Core/Services/TtsuImportService.cs`, `Kiseki.Web/Pages/Import/Ttsu.cshtml.cs`, `Kiseki.Web/Models/TtsuImportBatch.cs`, `Kiseki.Web/Models/TtsuBookPreviewViewModel.cs`, and `Kiseki.Web/Services/TtsuImportBatchStore.cs` (the store required no 4A change).
- Allowed 4B implementation files: `Kiseki.Web/Pages/Import/Ttsu.cshtml`, narrowly scoped additions to `Kiseki.Web/wwwroot/css/site.css` and `Kiseki.Web/wwwroot/js/site.js`, plus focused UI/render/browser tests in `Kiseki.Tests` (prefer a new `TtsuImportSelectionUiTests.cs`; do not rewrite the backend fixtures).
- Do not edit Core services/entities, migrations/snapshot, Import PageModels, batch-store/review records, Jiten clients, or Console in 4B. Report a concrete Core/PageModel contract gap instead.

**Deterministic UI fixtures and acceptance cases:**

1. Catalogue-only `Test Book`, canonical identity `subdeck:10:15`, zero copies: show `New copy under Test Book` as the provider-backed suggestion; confirmation leaves one installment and adds one Library copy.
2. `Volume 1` has an old bound copy with a 900-character log and exact identity `subdeck:10:101`; a different TTSU folder shows `New copy under Volume 1`, and confirmation leaves two bindings and two separate histories.
3. Two unbound copies titled `Same Book`, or two legacy copies claiming `subdeck:10:101`: render an ambiguity callout, do not preselect a winner, and disable confirmation until the reviewed explicit choice has no Core error.
4. A unique unbound copy displays `Existing copy`, its lifetime activity, and whether it is already bound. A bound copy from another source cannot be chosen as an overwrite target; present the Core error and separate-copy action.
5. Changing copy/installment selection, metadata candidate, cover edition, orphan assignment, daily resolution, or bookmark choice requires Review again. Expired batches show re-upload guidance; stale target/binding/provider evidence shows review-again guidance; replayed committed operation IDs show the existing success notice without another write.

**Required states/accessibility:**

- Render three compact, plainly labelled choices: existing copy, new copy under an existing catalogue installment, and new installment plus copy. Use a fieldset/legend per book, associated labels/descriptions, binding/history/provider badges with text (not color alone), keyboard-operable controls, visible focus, an accessible error summary, and an `aria-live` review/enrichment status.
- Preserve current upload/enrichment progress, daily/bookmark conflict controls, metadata/cover review, selected-book behavior, mobile layout, loading/disabled submission behavior, cover fallback, and antiforgery forms. Do not hide ambiguity behind an automatic default.
- Browser acceptance must prove upload -> reviewed catalogue target -> confirm -> Series/Library persistence using disposable SQLite and stubbed provider HTTP, plus expiry/stale/replay/error states. Run an automated WCAG 2 A/AA scan, keyboard flow, narrow viewport overflow check, and console/page-error check.

## Batch 4B implementation

- Replaced the legacy Create/Merge controls in `Kiseki.Web/Pages/Import/Ttsu.cshtml` with the three compact, plainly labelled target choices:
  1. **Update existing copy** (`TtsuCopyIntent.ExistingCopy`): offers selection among existing library copies, presenting lifetime character counts, source binding state, and warning badges for binding conflicts (`Bound to another source (conflict)`).
  2. **New copy under existing installment** (`TtsuCopyIntent.NewCopyUnderExistingInstallment`): offers selection among existing catalogue installments, presenting copy counts (including explicit `Catalogue only (0 copies)` and unbound counts) and provider keys.
  3. **New installment & copy** (`TtsuCopyIntent.NewInstallmentAndCopy`): creates a new standalone installment and library copy.
- Accessible structure & WCAG compliance:
  - Each book card uses a semantic `<fieldset class="ttsu-target-fieldset">` with `<legend class="ttsu-target-legend">` and `aria-labelledby`.
  - Radio options use `role="radiogroup"`, explicit `for`/`id` labels, and `aria-describedby` referencing contextual explanation text.
  - Target ambiguity renders an alert callout (`ttsu-target-ambiguous`) with `role="alert"` and `aria-live="polite"`. Provider-backed suggestions render with distinct `Suggested` text badges.
  - All status badges (`ttsu-badge-conflict`, `ttsu-badge-bound`, `ttsu-badge-unbound`, `ttsu-badge-catalogue-only`, `ttsu-badge-tracked`) use descriptive text rather than relying on color alone.
  - Added visible high-contrast keyboard focus outlines (`:focus`, `:focus-visible`) and max-width wrapping to prevent mobile layout overflow.
- Interactive and stale-review handling in `Kiseki.Web/wwwroot/js/site.js`:
  - `initTtsuTargetSelection` toggles the corresponding target dropdowns, syncs the hidden `Mode` field (`Create` vs `Merge`), and marks reviews stale upon selection changes.
  - `markReviewStale` disables the Confirm button with an explanatory tooltip, highlights the "Refresh review" action with an animated cue, and displays live status guidance (`data-ttsu-confirm-hint`).
  - Stale detection also monitors changes to candidate selections, cover editions, bookmark progress choices, day conflict resolutions, and orphan log assignments.
- Added comprehensive PageModel and markup tests in `Kiseki.Tests/TtsuImportSelectionUiTests.cs`:
  - `CatalogueOnly_Volume_SuggestedAsNewCopy_AndConfirmedLeavesOneInstallmentAndAddsLibraryCopy`: catalogue-only installment suggestion hydrates a new library copy without duplicating the installment.
  - `Reread_OldBoundCopyExists_DifferentSourceFolder_SuggestsNewCopyUnderInstallment_AndPreservesSeparateHistories`: reread from a different source folder detects old binding conflict, suggests a new copy under the same installment, and commits two separate copy histories.
  - `AmbiguousTargets_TwoUnboundCopies_RendersAmbiguity_DoesNotPreselectWinner_AndBlocksConfirmationUntilExplicitChoice`: ambiguous titles flag ambiguity, suppress default selection, and block confirmation until an explicit reviewed choice is made.
  - `TargetSelection_UnboundCopyShowsLifetimeChars_BoundCopyFromOtherSourceRejectsOverwrite`: bound copy from another source rejects overwrite with Core validation error; switching to a new copy under the installment resolves cleanly.
  - `StaleReview_ModifiedTargetChoice_RequiresReviewAgain_AndReplayReturnsCommittedReceipt`: unreviewed target modification is rejected with review-again requirement; refreshed review confirms cleanly; replaying the batch ID returns the committed receipt without repeat writes.
  - `MultipleCopiesUnderInstallment_DisplaysAllCandidateCopies_AndAllowsExplicitSelection`: multiple copies under an installment display their distinct lifetime activity and bindings, allowing explicit copy selection and targeted log merge.
  - `MultipleBooksInBatch_DistinctTargetChoices_ImportsBothAtomicallyWithSeparateHistories`: multi-book batch with mixed intents (new copy under installment and update existing copy) commits atomically in a single transaction with separate histories.
  - `RazorMarkup_ContainsRequiredAccessibilityAndBatch4BContractElements`: asserts required fieldsets, legends, radiogroup, intent values, action labels, badges, descriptors, and button hooks in `Ttsu.cshtml`.

### Batch 4B integration review and fixes

- The Gemini work was present directly in the dirty `main` checkout, not on a separate branch. The actual Razor/CSS/JavaScript/test diff was reviewed against the Batch 4A handoff; unrelated changes, including the deleted cover prompt and untracked agent-prompts document, were preserved.
- Core and the existing Import PageModel remain the owners of target matching, reviewed fingerprints, target/provider/version validation, stale-review decisions, copy preservation, atomic commit, receipts, and friendly domain errors. The UI posts only intent and target correlation IDs through Razor antiforgery forms; it does not accept posted titles, provider metadata, or versions as authority.
- Fixed an in-scope target-switching defect: hidden inactive copy/installment selects remained enabled and retained their previous IDs, so changing intent could post both `TargetId` and `InstallmentId`. The rendered inactive selector is now disabled, JavaScript clears and disables the selector whenever its intent becomes inactive, initialization does not falsely stale a review, and focused markup coverage locks down that contract.
- Added reduced-motion handling for the stale-review pulse. A user with `prefers-reduced-motion: reduce` still receives the textual live status and disabled confirmation state without animation.
- The first live WCAG scan found the Bootstrap blue Cancel link at 4.25:1 contrast on the dark canvas. The review Cancel action now uses the accessible outlined-secondary treatment; the rebuilt follow-up scan reports no WCAG 2 A/AA violations.
- Existing ambiguous-target callouts, semantic badges, associated labels/descriptions, live stale guidance, loading/disabled submission behavior, and Core error rendering were retained. No Core, PageModel, batch-store, migration, Jiten client, or Console contract change was required.

### Batch 4B browser-to-persistence verification

- Ran a live Development Kestrel process against a disposable SQLite file and used `agent-browser` with the repository TTSU statistics fixture. Through the browser, created `Browser Test Series`, added a catalogue-only `Test Book` installment, uploaded the folder, and received the provider-independent `New copy under existing installment` review choice.
- Changed target intents by keyboard. Each change disabled confirmation, exposed the live `Refresh review` instruction, disabled and cleared the inactive selector, and required a server review before confirmation was enabled again.
- Confirmed the reviewed target and observed the Library redirect. Series Details retained one `Test Book` installment with its new tracked copy; copy Details showed 18,610 lifetime characters in two imported sessions. This proves the browser form, antiforgery, PageModel, Core transaction, and persisted relationship/activity path with disposable data.
- Axe 4.12.1 WCAG 2 A/AA reported 0 violations after the contrast fix. One select contrast check remained `incomplete` because axe could not determine the Bootstrap background image; its visible state was inspected. At 390x844 the document width equalled the viewport width (390 px), reduced-motion produced `animation-name: none`, and browser console/page-error checks were empty.
- Expiry, stale local/provider/binding evidence, competing targets, atomic rollback, and lost-response receipt replay remain deterministic server-side/PageModel regression cases; they were not induced by waiting for expiry or corrupting the live disposable browser database.
- The browser session and Kestrel process were stopped after verification. The disposable database and upload fixture were removed after the final test run.

## Book-series milestone acceptance

The complete Batch 1A-4B book-series workflow was exercised on 2026-09-23 with a new disposable SQLite database, deterministic local Jiten HTTP responses, the repository TTSU fixture, the real Razor Pages UI, and the Console application. No live provider or production data was used.

- Created `Milestone Manual Books`, then added and reordered released/included, released/excluded, upcoming, and unknown-release/unknown-total installments. The final Series summary reported three eligible released installments, one upcoming, one unknown-release, and one excluded entry.
- Found and fixed a real form defect during this exercise: an unchecked `IsIncluded` checkbox posted no value while both input records defaulted to `true`. Add and edit forms now post an explicit `false` fallback, and a markup regression test covers both forms. The excluded installment remained excluded after a fresh persisted read.
- Created two distinct tracked copies under `Volume-One`, completed only the e-book copy, and observed headline progress of 45.5%: the installment contributed its 100,000 canonical characters once to the 220,000-character computable denominator despite having two copies. Separate copy rows and histories remained intact.
- Hydrated `Milestone Jiten Books` from a deterministic complete two-child deck fixture. The first apply added two catalogue installments and two identities. A changed complete fixture then revised child 101, retained child 102, and added child 103; the second apply added one volume/identity and updated two metadata records. Hydration created no tracked copies and therefore added nothing to Library.
- Uploaded the repository TTSU statistics fixture, explicitly selected `New copy under existing installment` for the existing `Test-Book` catalogue entry, refreshed the authoritative review, and confirmed it. The result was one new copy with 18,610 lifetime characters, 89 minutes, and two sessions under the existing installment. Replaying the exact confirmation returned the committed result and left the database at one TTSU copy rather than duplicating data.
- Library contained exactly the three tracked copies (`Test Book` and the two `Volume-One` editions); excluded, upcoming, unknown-metadata, and Jiten-hydrated catalogue-only entries remained visible in Series but absent from Library. Console `View library` against the same disposable database showed those same three copies, their canonical series membership, imported activity, and completed status, then exited successfully.
- A final authenticated Series Details axe WCAG 2 A/AA scan reported zero violations. Eight `color-contrast` checks were marked incomplete only because the controls contain the ▲/▼ glyphs; every affected reorder button has an explicit `Move Up` or `Move Down` accessible label. The prior real contrast findings were fixed by using the primary Jiten-refresh button treatment and dark text on the Upcoming badge. Browser console and page-error reports were empty.

Migration and upgrade evidence:

- `20260921172122_AddCanonicalInstallments` and `20260922202102_AddJitenCatalogueRefreshState` are registered and their generated designers/snapshot are present. `dotnet tool run dotnet-ef migrations has-pending-model-changes --project Kiseki.Core --startup-project Kiseki.Core --no-build` reports no pending model changes.
- The passing SQLite suite covers fresh creation; physical old-schema additive upgrade; exact IDs, logs, bindings, receipts, and overrides; standalone/cross-type legacy rows; duplicate provider claims; repeat and interrupted/retried upgrade; and concurrent startup without recreating the database.
- PostgreSQL execution is still absent: `KISEKI_TEST_POSTGRES` is unset, and `postgres`, `pg_ctl`, `docker`, and `podman` are unavailable. All six PostgreSQL migration/import concurrency tests were skipped. A disposable PostgreSQL run, production backup, and upgrade rehearsal remain mandatory before release.

Milestone disposition: **functionally complete through Batch 4B on SQLite; not yet approved for PostgreSQL production release**. No franchise, anime, deployment, or destructive production operation was performed.

## Batch 5A implementation

- Added `FranchiseCatalogueService` as the Core boundary for `CreateFranchiseCommand`, `EditFranchiseCommand`, `MoveSeriesToFranchiseCommand`, `UnassignSeriesFromFranchiseCommand`, and `DeleteFranchiseCommand`. Moving a series moves only its franchise membership; its installments, copies, source bindings, and activity remain attached to that series. Deletion explicitly clears `MediaSeries.FranchiseId` before removing the franchise; the existing set-null foreign key remains the database safeguard.
- Added bounded no-tracking `FranchiseCatalogueDetails` and `FranchiseSeriesSummary` reads. Each member series keeps its own media type and native result: Book rows expose the existing `SeriesProgressResult` and character/minute lifetime activity; unsupported media exposes `FranchiseProgressUnit.NotAvailable` with no invented percentage. There is deliberately no franchise-level progress field or cross-media aggregation.
- Added `JitenFranchiseTopologyService`, `JitenFranchiseTopologyPlanner`, immutable provider node/edge/proposal/review/receipt records, and the expiring `IJitenFranchiseTopologyReviewStore`. It fetches the graph outside the transaction, fingerprints provider and local evidence, requires a complete choice set, refetches before apply, and then checks local evidence inside a serializable transaction.
- Graph decisions are node-level only. Jiten media type 4 is classified as Book; every other media type is `Unknown` and can only be ignored or kept unresolved. Edges are exposed as evidence but are never converted into ordering, series merges, or implicit membership moves. A Book node can create a **separate** series or explicitly link one compatible series; one reviewed topology cannot use the same series for two nodes, and a series with a different Jiten deck identity is rejected.
- Incomplete, truncated, duplicate, malformed, or bounded-out graphs prominently carry a warning and permit only `Ignore`/`KeepUnresolved`; they cannot create, move, or link a series. Refresh never unassigns a series merely because a node is absent. Existing ignored and unresolved decisions persist in `JitenFranchiseGraphNodeStates` and are surfaced again on the next preview.
- Added durable `JitenFranchiseTopologyReceipt` replay results. An operation ID is checked before review lookup, so a retry after a lost response returns the original receipt even if the in-memory review store has restarted. Provider or local fingerprint changes require a new review and make no membership changes.
- Added the generated PostgreSQL migration `20260923202311_AddJitenFranchiseTopologyState` and designer/snapshot. It creates only `JitenFranchiseGraphNodeStates` and `JitenFranchiseTopologyReceipts`; it does not rewrite existing catalogue, series, copy, or history rows. `SqliteSchemaUpgrade` creates the same tables/indexes transactionally with `CREATE TABLE/INDEX IF NOT EXISTS`, without recreating an existing local database or applying PostgreSQL migrations.
- Registered `FranchiseCatalogueService`, `IJitenFranchiseTopologyReviewStore`, and `JitenFranchiseTopologyService` in Web dependency injection. No franchise Razor page, navigation, Console screen, or anime feature was implemented in this batch.

### Batch 5A regression evidence

- `FranchiseCatalogueAndTopologyTests` covers manual create/move/unassign/delete with preserved installment/copy relationships; mixed book/anime summaries with no cross-media percentage; complete graph linking plus a separately created side-series; unknown-media unresolved state; truncated graph membership protection; ignored-state persistence; provider and local stale reviews; and lost-response receipt replay.
- `CanonicalInstallmentSchemaTests` now asserts that an upgraded physical legacy SQLite database contains empty franchise graph state/receipt tables while preserving the prior data. `DatabaseMigrationTests` asserts the new migration is registered and the model snapshot has no pending changes.
- Focused franchise/schema/migration run: **14 passed, 0 failed, 0 skipped**. Full solution run on 2026-09-23: **752 passed, 0 failed, 6 skipped, 758 total**. The six skipped tests are the pre-existing environment-gated PostgreSQL migration/concurrency tests; no PostgreSQL execution occurred.

### Batch 5B UI handoff

#### Stable Core contracts

- Manual pages use `FranchiseCatalogueService`: `CreateAsync(CreateFranchiseCommand)`, `EditAsync(EditFranchiseCommand)`, `MoveSeriesAsync(MoveSeriesToFranchiseCommand)`, `UnassignSeriesAsync(UnassignSeriesFromFranchiseCommand)`, `DeleteAsync(DeleteFranchiseCommand)`, and `GetDetailsAsync(Guid franchiseId, int seriesTake = 100)`. Map `MediaCatalogConflictException`, `ArgumentException`, and `ArgumentOutOfRangeException` to field/form errors; do not set `FranchiseId` directly.
- Render `FranchiseCatalogueDetails` and `FranchiseSeriesSummary` as supplied. `BookProgress` may be null, `ProgressUnit.NotAvailable` means display the media-specific unavailable state, and no page may calculate or display a combined franchise percentage.
- Graph preview uses `JitenFranchiseTopologyService.PreviewAsync(franchiseId, anchorDeckId)`, then `Approve(reviewId, fingerprint, choices)`, then `ApplyAsync(operationId, franchiseId, reviewId, approvalFingerprint)`. The browser posts only franchise/anchor IDs, server-held review IDs/fingerprints, node proposal IDs, action enum values, optional selected series IDs for link actions, and a generated operation ID. It must not post/trust graph titles, provider classifications, edges, fingerprints it computed, or a target deck identity.
- `JitenFranchiseTopologyReview` supplies `IsCompleteGraph`, `IsTruncated`, `Warning`, nodes, edges, proposals, recommended/allowed actions, and persisted resolution. `JitenFranchiseTopologyReviewRequiredException` means show a non-destructive “review again” state. Lookup `GetReceiptAsync(operationId, franchiseId)` only within that franchise boundary. Handle cancellation and Jiten HTTP exceptions as friendly preview errors; retain no partially applied choices.
- UI must send exactly one `JitenFranchiseTopologyChoice` per supplied proposal. `CreateSeparateBookSeries` and `LinkExistingSeries` are only available when Core marks them allowed; never enable them on a truncated/incomplete review. A link must include an explicit series selection unless `ExactSeriesId` is supplied. `Ignore` and `KeepUnresolved` are durable user decisions; show them as such and do not silently reset them on refresh.

#### Allowed Batch 5B files

- New: `Kiseki.Web/Models/FranchiseViewModels.cs`, `Kiseki.Web/Pages/Franchises/Index.cshtml`, `Index.cshtml.cs`, `Create.cshtml`, `Create.cshtml.cs`, `Edit.cshtml`, `Edit.cshtml.cs`, `Details.cshtml`, `Details.cshtml.cs`, and `Kiseki.Tests/FranchisePageTests.cs`.
- Existing UI-only files: `Kiseki.Web/Pages/Shared/_Sidebar.cshtml`, `Kiseki.Web/wwwroot/css/site.css`, `Kiseki.Web/wwwroot/js/site.js`, and this progress document.
- Do not edit Core entities/services/DTOs, migrations, `SqliteSchemaUpgrade`, Web `Program.cs`, Series pages, TTSU import PageModels/batch store, Console, or existing Batch 1-4 tests. Report a Core contract gap instead of working around it in Razor or JavaScript.

#### Deterministic fixtures and acceptance cases

1. Manual fixture: one franchise with `Main novels` (Book), `Side novels` (Book), and `Anime adaptation` (Anime). Move and unassign each member; delete the franchise and assert all three series, book installments, copies, logs, and bindings survive with null franchise IDs. Details show two separate Book metrics and an Anime unavailable/native-unit row, never a total percentage.
2. Complete graph fixture anchored at deck 10: Book node 10 `Main novels`, Book node 20 `Side novels`, Unknown node 30 `Anime`, with edges 10→20 and 20→30. Link existing deck-10 series, create a separate deck-20 Book series, keep node 30 unresolved. Confirm two separate series rather than a merged progression line, a persisted unresolved node, receipt result, and PRG notice.
3. Truncated graph fixture: deck 10 and 20 with `Truncated = true`. Render the warning before choices; Core must expose no create/link action. Persist Ignore/Unresolved only and prove existing series membership remains unchanged.
4. Stale/replay fixture: change provider title after Preview or change franchise title/membership before Apply; show review-again without writes. Confirm once, then resubmit the same operation ID after constructing a fresh review store; show the stored receipt with no duplicate series/state rows.
5. Browser flow: create franchise, assign two existing Book series, preview a complete stub graph, select link/create/unresolved choices, confirm, reload Details, then refresh a truncated stub. Verify antiforgery, keyboard-labelled action controls, live loading/error/review state, mobile wrapping, no console errors, and no cross-media percentage.

---

## Batch 5B implementation

- Built the complete persisted Franchise workflow across Razor Pages at `Kiseki.Web/Pages/Franchises/`:
  - `Index.cshtml` & `Index.cshtml.cs`: lists franchises ordered by title with member series counts by media type (total series, books, anime, other), optional Jiten anchor deck badge, empty state with creation link, and notice/error alerts.
  - `Create.cshtml` & `Create.cshtml.cs`: creates a new franchise with required trimmed title and optional positive Jiten anchor deck ID, redirecting to Details via PRG.
  - `Edit.cshtml` & `Edit.cshtml.cs`: edits franchise title and optional anchor deck ID; provides safe container deletion that unassigns member series (`FranchiseId = null`) while preserving all member series, canonical installments, tracked copies, immersion logs, and bindings intact without data loss.
  - `Details.cshtml` & `Details.cshtml.cs`:
    - Renders franchise title, anchor deck badge, truncation warning if member count exceeds 100, and edit link.
    - Displays member series grouped cleanly into Book Series, Anime Series, and Other Media Series.
    - Strictly preserves separate media units: Book series report character progress (`ProgressUnit == FranchiseProgressUnit.Characters`), headline percentage, progress bar, and lifetime characters/minutes; Anime and other media series display native/unavailable status (`ProgressUnit == FranchiseProgressUnit.NotAvailable`) with explicit "Progress tracking not available for this media type" and NO progress bar. Absolutely no combined cross-media franchise progress percentage is computed or rendered.
    - Per-series unassign action calling `FranchiseCatalogueService.UnassignSeriesAsync` with antiforgery and confirmation.
    - Move series form calling `FranchiseCatalogueService.MoveSeriesAsync` using an accessible dropdown of available unassigned or cross-franchise series.
    - Jiten graph discovery preview form accepting positive anchor deck ID and calling `JitenFranchiseTopologyService.PreviewAsync`.
    - Topology review panel rendering node classification badges (Book vs Unsupported Media Type), proposal actions with allowed-action radios, candidate series dropdowns for `LinkExistingSeries`, persisted resolution notices, truncation warning callout, and expiry indicator.
    - Topology apply form calling `JitenFranchiseTopologyService.Approve` and `ApplyAsync` with review correlation tokens and submit-lock prevention, redirecting to Details with the operation ID.
    - Durable topology receipt display with stats grid (Created Series, Linked Series, Ignored Nodes, Unresolved Nodes) and `Replayed Result` indicator on lost-response replay.
- Added primary navigation link for "Franchises" right below "Series" in `Kiseki.Web/Pages/Shared/_Sidebar.cshtml`.
- Added scoped dark-mode styling to `Kiseki.Web/wwwroot/css/site.css` for `.bg-surface`, `.franchise-card`, `.topology-review-box`, and `.series-row`.
- Added comprehensive unit and PageModel test suite in `Kiseki.Tests/FranchisePageTests.cs` (10 tests, all passing):
  - `Index_ListsFranchises_AndEmptyStateWhenNone`: empty state and populated state with media type counts.
  - `Create_ValidInput_CreatesFranchiseAndRedirectsToDetails`: valid creation and route redirection.
  - `Create_InvalidInput_ReturnsPageWithValidationError`: model validation rejection.
  - `Edit_UpdatesFranchise_AndDeleteUnassignsMembersWithoutDataLoss`: updates title/anchor; verifies delete clears `FranchiseId` while preserving all series, installments, copies, logs, and bindings.
  - `Details_DisplaysGroupedMediaWithoutCrossMediaPercentage`: verifies Book character progress and Anime unavailable state coexistence with no combined percentage.
  - `MoveAndUnassignSeries_UpdatesFranchiseMembershipCorrectly`: moves series between franchises and unassigns cleanly.
  - `TopologyPreviewAndApply_CompleteGraph_CreatesAndLinksSeriesWithReceipt`: complete graph preview, node action choices, apply, and receipt display.
  - `TopologyPreview_TruncatedGraph_ShowsProminentWarningAndOnlyIgnoreOrUnresolved`: truncated graph warning and restriction of allowed actions to Ignore and KeepUnresolved only.
  - `TopologyApply_StaleReviewOrChangedProvider_RequiresReviewAgain`: provider change before apply catches review-required error and requires review again without writes.
  - `TopologyApply_ReplaySameOperationId_ReturnsCommittedReceipt`: replaying committed operation ID returns stored receipt with no duplicate writes.

---

## Prompt C final integration and acceptance

- Reviewed the actual Prompt A/B Core, immutable result, PageModel, and Razor changes. The pages consume `SeriesProgressResult` through the existing view models; Index and Details do not recalculate progress. No Core contract, persistence, or workflow defect was found. Visual verification did find one reduced-scope presentation defect: Bootstrap's `text-muted !important` made tracking and technical-coverage text too faint on the dark Details summary. A scoped dark-surface override now restores the intended theme color, with one focused stylesheet regression; no schema or migration changed.
- A focused test run covering Series catalogue/progress/pages, reviewed Jiten refresh, TTSU installment import/pages, franchise catalogue/pages, Jiten linking, and existing Console-adjacent behavior passed **136 tests, 0 failed, 0 skipped**. Existing regressions prove the four-volume acceptance case, multiple copies counted once, missing totals with partial-coverage labels, no eligible volumes, and the 501-volume case where Details renders 500 rows but its whole-series totals exactly match Index.
- Added `artifacts/merge-validation/browser/run_prompt_c_browser_verification.cjs` as a repeatable disposable acceptance harness. It starts a local Jiten stub and Kestrel against a fresh SQLite file, uses real Razor/antiforgery interactions, and removes the database and upload fixture afterward. The preferred `agent-browser` CLI was unavailable in this checkout/environment, so the established local Playwright/Chrome harness was used instead.
- The persisted browser workflow created a Book series at `/Series/Create`, populated four catalogue-only volumes through `/Series/Details/{id}` and the reviewed Jiten refresh, exposed the provider's unknown release/missing character total, and saved explicit release-state and character-count corrections. It then imported Volume 1 as complete and Volume 2 at 50% through `/Import/Ttsu`, targeting the existing catalogue installments.
- Persistence stayed at **4 installments** while tracking changed from 0 to 2 installments. Adding a second copy of Volume 1 raised physical copies to 3 without changing the unique tracked count or completion. Final Index and Details results were identical: **1 / 4 completed**, **2 tracked, 2 not tracked**, and **37.5%** character progress. Volumes 3 and 4 remained catalogue-only and absent from `/Library`.
- A second complete reviewed Jiten refresh preserved both manual corrections, all 3 copies, both TTSU bindings, both immersion logs, and the completed-copy state. The final browser run reported no page errors, actionable console errors, or unhandled server exceptions. The captured desktop Details and Index states were visually inspected.
- Existing franchise tests and the full solution suite remained green; no franchise implementation was removed or broadened. Existing Console tests/coverage remained unchanged and green. No live provider, production database, deployment, or production data was touched.

### Short user guide

1. Open `/Series/Create`, enter the title, choose **Book**, and create the series.
2. On `/Series/Details/{id}`, use **+ Add volume** for manual catalogue entries, or **Refresh from Jiten**, enter the deck ID, fetch the preview, review every proposal, and apply it.
3. Correct incomplete catalogue metadata on the same Details page with **Set release state** or **Edit**, then save. Catalogue-only volumes appear here but not in `/Library`.
4. Use **+ Track copy** to create a copy manually. To import reading, open `/Import/Ttsu`, select the TTSU folder, preview it, choose **New copy under _existing volume_** when offered, refresh the review if requested, and confirm.
5. Read the whole-series summary on `/Series` or `/Series/Details/{id}`. Both show released/included completion, unique tracked/untracked volume counts, character progress, and partial metadata coverage; `/Library` continues to show tracked copies only.

### Remaining release blockers

- The local feature workflow is complete on disposable SQLite. `dotnet test Kiseki.slnx` passed **771**, failed **0**, and skipped **6** on 2026-09-24.
- The six environment-gated PostgreSQL migration/concurrency tests were **skipped**, not passed, because `KISEKI_TEST_POSTGRES` was not configured. Running them against disposable local PostgreSQL remains a separate release prerequisite.
- A production backup and migration/upgrade rehearsal also remain release prerequisites. They do not require or justify more feature development, and no production operation was attempted here.

## Verification

Command: `dotnet test Kiseki.slnx`

Latest result on 2026-09-24 after Prompt C:

- Passed: 771
- Failed: 0
- Skipped: 6
- Total: 777

Skipped tests:

- `PostgreSql_UpgradesExistingDatabaseWithoutLosingLegacyRows`
- `PostgreSql_ConcurrentUpdatesRequireARefreshedReview`
- `PostgreSql_LostCommitResponseReturnsReceiptOnRetry`
- `PostgreSql_ConcurrentFirstImportsCreateOnlyOneWork`
- `PostgreSql_CanonicalBackfillKeepsDuplicateProviderClaimsForReview`
- `PostgreSql_ConcurrentCanonicalMigrationCreatesOneInstallmentPerWork`

All six PostgreSQL tests were skipped because `KISEKI_TEST_POSTGRES` was not configured; no PostgreSQL migration or concurrency execution occurred in this review environment. This remains a release blocker, not a local Prompt C functional blocker.

SQLite execution covers fresh creation, a physical old schema, exact ID/log/binding/receipt/override preservation, standalone and cross-type legacy membership, duplicate and unique provider claims, repeat execution (including hydrated installment IDs distinct from copy IDs), a simulated interrupted transaction followed by retry, simultaneous startup, catalogue query models, all Series page/workflow fixtures, Batch 3A refresh idempotence/concurrency/stale-review/disappearance/protection/replay cases, Batch 3B ownership/error/PageModel cases, Batch 4A installment/copy intent, reread, ambiguity, stale-version, competing-binding, and receipt-replay cases, all Batch 4B UI contract and multi-copy selection fixtures, Batch 5A Core manual franchise commands and topology services, all Batch 5B Franchise PageModel, unit-separation, and review UI tests, Prompt A whole-series count/coverage/row-limit regressions, and the Prompt C persisted browser workflow.

## Proposed sequence

1. **Batch 0:** complete; decisions accepted and Core contracts recorded.
2. **Batch 1A:** implemented; SQLite gates pass, PostgreSQL execution remains the release blocker.
3. **Batch 1B:** complete; shared creation/assignment/identity commands migrated Core, Web, and Console writers/readers while retaining compatibility fields.
4. **Batch 2A:** complete; pure book-series progress calculator, catalogue commands, immutable query results, and Batch 2B handoff are ready.
5. **Batch 2B:** complete after integration review; persisted manual Series UI, corrected Core ownership/stale-review contracts, regression tests, and restart-backed browser-to-SQLite verification satisfy the exit criteria.
6. **Batch 3A:** complete; completeness-aware Jiten reconciliation, expiring reviewed choices, transactional apply, durable receipts, and additive schema are tested on SQLite.
7. **Batch 3B:** complete after integration review; receipt/review ownership is enforced in Core, the restart-breaking SQLite backfill is fixed, accessible loading/error/review/receipt states are present, and the persisted browser-to-SQLite/restart flow satisfies the exit criteria.
8. **Batch 4A:** complete; target intent/review contracts, provider/source precedence, transactional installment/copy persistence, stale/race validation, replay, PageModel plumbing, and regression fixtures are in place.
9. **Batch 4B:** complete after integration review; inactive target IDs are suppressed, reduced-motion and contrast defects are fixed, and the persisted browser-to-SQLite flow plus the comprehensive PageModel/markup suite satisfy the exit criteria.
10. **Book-series milestone:** accepted on disposable SQLite; full regression, browser-to-persistence, Library-boundary, Console, migration-snapshot, and replay checks pass. PostgreSQL execution and release rehearsal remain open.
11. **Batch 5A:** complete; manual franchise commands, per-series unit-separated summaries, durable ignored/unresolved graph state, reviewed topology apply/replay, and additive upgrades are tested on SQLite. Batch 5B is the next UI-only delivery.
12. **Batch 5B:** complete; persisted franchise Index/Create/Edit/Details pages, sidebar navigation, media grouping without cross-media percentage, and graph topology review/receipt workflows verified.
13. **Shortened Prompt A:** complete; explicit released/included completion and tracking contracts plus row-limit-independent whole-series summaries are verified.
14. **Shortened Prompt B:** complete; presentation-only Series simplification consumes the Prompt A contracts.
15. **Shortened Prompt C:** complete; final review, focused regression coverage, and persisted browser acceptance passed on disposable SQLite.
16. **Anime/franchise expansion:** deferred; do not resume the old Batches 6/7 sequence without new authorization.

Wide books should ship before franchises; manual anime should remain useful without a provider. Provider sync must always preview changes and preserve manual corrections.

## Next authorization

No next feature batch is authorized. The shortened A/B/C scope is complete. Anime work and further franchise expansion remain explicitly deferred; do not resume the old Prompt 13 or Batches 6/7 sequence without new authorization.
