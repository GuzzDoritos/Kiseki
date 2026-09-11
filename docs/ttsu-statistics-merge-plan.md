# TTSU statistics merge implementation plan

Status: implemented. See [manual testing](ttsu-merge-manual-testing.md) for fixture-based walkthroughs, existing-database checks, and local/Render setup. The sections below preserve the design rationale.

Objective: repeatedly import TTSU statistics into an existing Kiseki database without duplicating reading, losing newer statistics, or depending on the current library title. Keep the merge engine reusable when Google Drive becomes an input later.

## Current behavior and gaps

The repository already implements a basic web merge:

- `Kiseki.Core/Services/TtsuBookImporter.cs`: `MergeInto` replaces existing TTSU values for matching dates and adds new dates. Other sources and dates absent from the upload are preserved.
- `Kiseki.Web/Pages/Import/Ttsu.cshtml.cs`: preview and confirmation match normalized titles; duplicate library titles select the first result. Confirmation runs inside a transaction.
- `Kiseki.Core/Services/TtsuSessionMapper.cs`: mapping discards `LastStatisticModified`. The importer uses it only to choose among repeated dates within one incoming file.
- `Kiseki.Web/Models/TtsuBookPreviewViewModel.cs`: preview totals use raw entries, while import collapses repeated dates. Preview can therefore disagree with the saved result.
- Web uploads with the same normalized title are reduced to one file based on its maximum entry timestamp. This can discard dates or newer individual days from other files.
- `Kiseki.Console/Screens/AddMediaScreen.cs`: the console always creates a new work.
- Existing tests cover basic replacement and insertion, but not stale exports, ambiguous matches, legacy duplicates, or concurrent imports.

Consequences: an older export can overwrite newer values; identical uploads are reported as updates; renaming or choosing a Jiten title can break matching; existing duplicate TTSU rows survive because only the first row for a date is updated.

This plan assumes the existing PostgreSQL schema in the repository. It does not assume that a live database has been inspected. SQLite remains the fast test provider.

## Recommended merge contract

Treat each TTSU entry as the total for one book on one date. The upstream [TTSU documentation](https://github.com/ttu-ttu/ebook-reader#statistics-merge-mode) confirms daily tracking and choosing newer daily revisions. Keep the original source revision as a nullable integer; do not substitute file modification or import time. Unknown revisions and conflicting device histories still require explicit review.

The effective key is the confirmed TTSU book binding plus `DateOnly`. Replace characters and reading time together from the same revision. For example, 5,000 characters and 30 minutes revised to 7,000 and 42 minutes becomes 7,000 and 42, not 12,000 and 72.

| Incoming state | Action |
| --- | --- |
| Date does not exist | Add one daily TTSU row. |
| Source revision is newer | Replace both values; allow decreases and zero as legitimate corrections. |
| Source revision is older | Preserve the database values; report stale input. |
| Same revision and same values | No change. |
| Same revision but different values | Conflict requiring review; never use file order as authority. |
| Either revision is unknown and values differ | Review the old and incoming values before replacement. |
| Either revision is unknown and values agree | No statistics change; establishing a missing revision requires the baseline policy below. |
| Newer revision with unchanged values | Advance the stored revision without reporting a statistics update. |
| Existing date is absent from the upload | Preserve it. An incomplete export does not establish deletion. |
| A non-TTSU log exists on the same date | Preserve it independently. |

Repeating an accepted import must leave the same logs, values, revisions, and totals. A revision must never move backwards automatically. Exact comparison is appropriate for integer characters; centralize a documented reading-time comparison/normalization consistent with seconds-to-minutes conversion and existing stored doubles.

Keep user-managed titles, Jiten links, character-count overrides, completion flags, and series/franchise assignments. Progress continues to derive from existing domain calculations. Editing TTSU rows locally would require an explicit override policy before enabling such an editor.

## Implementation sequence

### 1. Normalize incoming statistics once in Core

Introduce an immutable daily snapshot model containing date, characters, reading time, and nullable source revision. Use one normalizer for create, merge, and preview.

Preserve file/folder origin through parsing. For multiple files confirmed to represent the same source book, reconcile the union of their dates using per-date revisions. Equal revisions with different payloads become conflicts. Do not discard a whole file because another contains one newer entry, and do not combine unrelated same-title books.

Validate null entries, dates, values, missing/invalid revisions, and conflicting identity hints. Treat absent/zero revisions as unknown unless representative exports establish different semantics. Do not convert source date keys through a timezone.

### 2. Establish persistent book matching

Introduce a TTSU binding associated with a `MediaWorkId`. For this slice, allow one active TTSU history per work; separate-device histories must resolve to that history or require review.

Matching order:

1. A previously confirmed source binding.
2. A unique normalized-title candidate suggested for user confirmation.
3. An explicit choice of an existing book, creation of a new book, or skip.

Multiple candidates must never resolve with `First()`. The selection carries the explicit target work ID, and confirmation validates it against current database state. A target that disappears before confirmation requires a refreshed decision; it must not silently become a new work.

Store the original import title separately from the display title so a Kiseki/Jiten rename preserves the association. Retain the relative book folder as a matching hint. The modeled JSON contains no stable book ID: titles and paths cannot guarantee identity across source renames, moves, or collisions. Persist confirmed associations, but require rematching when hints are ambiguous. Do not invent an ID from a changing statistics payload.

The binding gets its own internal ID. Later, a verified external source identifier can attach to that same binding. Google Drive should not create a second set of `Source = "drive"` reading logs for the same TTSU history.

If the user explicitly creates another copy, make its future import destination explicit: retain the current binding or deliberately reassign it. A single source must not silently update both copies.

### 3. Add metadata without rewriting existing history

Generate an additive migration and snapshot using the repository's local tool:

```powershell
dotnet tool run dotnet-ef migrations add AddTtsuImportState --project Kiseki.Core --startup-project Kiseki.Core
```

Proposed persistence:

- A TTSU binding table with work ID, confirmed matching hints, and an application-managed concurrency token. Enforce one binding per work.
- Nullable binding ID and nullable source revision on `ImmersionLog`. Preserve the existing work relationship and IDs. Enforce consistency between a bound log's work and its binding.
- A unique index on `(TtsuBindingId, Date)` for bound rows. Legacy unbound rows and manual sessions remain valid; only TTSU rows may receive this binding.
- A small committed-import receipt keyed by operation/batch ID, with result counts. Save it in the same transaction as the changes to prevent repeated confirmation from creating another copy, including after a lost response.

Existing logs start with unknown provenance. Do not backfill invented source revisions or silently remove duplicates during startup migration.

On the first update of an existing work, preview a baseline adoption: show matching days and their before/after values, then let the user accept the incoming baseline or retain current values. Adoption attaches existing rows in place. Equal values may acquire a known incoming revision as part of that confirmed baseline; unequal values require the displayed replacement decision. Retaining a legacy value leaves its revision unknown, so a later changed input still needs review. Rows absent from the upload remain preserved with unknown revisions.

Audit duplicate TTSU rows for each work/date before binding that work. Present a separate resolution using existing values and the incoming baseline; never sum duplicate snapshots or select an arbitrary winner. Do not attach or update that work until its duplicates are resolved. Other works can proceed. Orphan logs also need explicit association before adoption. This lets the additive migration succeed on an existing database without guessing its history.

Test the migration against a disposable PostgreSQL database populated with the previous schema, including duplicate and orphan rows. SQLite `EnsureCreatedAsync()` does not verify a PostgreSQL upgrade. Before actual deployment, take and verify a restorable database backup; the current initializer's file-copy branch does not back up PostgreSQL.

### 4. Separate planning from applying changes

Build a Core planner that accepts normalized incoming snapshots and existing state and produces immutable per-day decisions: added, changed, unchanged, stale, or conflict. Include before/after values, aggregate deltas, metadata-only advances, and the selected target ID.

Build a Core application service that loads tracked entities, validates selections, recomputes the plan, applies accepted decisions, and commits a receipt. Both Web and Console call this service. Keep database orchestration and merge policy out of Razor/PageModel implementations.

Preview is read-only (`AsNoTracking`) and uses the same normalized decisions as commit. On confirmation, reload current state and compare it with the reviewed plan. If material changes affect the decision, return an updated preview. Never apply stale before/after values or trust posted totals/revisions.

Use a transaction plus an optimistic concurrency token on the binding, advanced by every importer, and uniqueness constraints for inserted dates/bindings. On a collision, roll back and replan from fresh state; do not retry stale entity mutations. A future background caller can recompute safe decisions and defer conflicts. Replayed operation IDs return the committed result, while a new upload of identical data succeeds with zero statistical changes.

### 5. Make updates understandable in Web and Console

Extend the existing preview to show the target book, matching reason, and counts of new days, changed days, unchanged days, stale days, and conflicts. Expand individual days to compare current and incoming characters/time, including decreases. Show the net change and resulting totals instead of presenting incoming totals as newly added reading.

Offer update-existing, choose-another-target, create-copy, and skip. For unknown baselines and conflicting revisions, offer explicit keep-current/use-incoming decisions. Changing the target recomputes the preview. Keep-current on an unresolved revision must not mark the incoming revision as accepted. An exceptional explicit replacement by older data must be labeled as a baseline reset, not an ordinary newer update.

Block confirmation of unresolved selected books; allow users to deselect them and commit the rest in the existing single transaction. Validate mode enums and target IDs server-side. Preserve preview expiration and Post/Redirect/Get notices. A no-op import should say that statistics are already up to date.

Give the console the same matching and review rules rather than its unconditional creation path. Future automated imports can apply unambiguous changes and report items needing manual review through the same Core result types.

### 6. Verify behavior and existing-database compatibility

Add meaningful tests covering:

- Updated daily totals plus new dates; repeat upload with no changes; valid decreases/zero; preservation of absent dates and manual logs.
- Newer, older, equal, missing, and conflicting revisions, including metadata-only advancement.
- Repeated dates and multiple files with complementary histories; conflicting ties; different same-title books.
- Renamed display titles, explicit target selection, duplicate-title ambiguity, and deleted targets.
- Baseline adoption, keeping unknown legacy values, duplicate/orphan resolution, and unchanged metadata/IDs.
- Preview totals matching committed results, including net changes and no-op reporting.
- Duplicate confirmation, concurrent insertion/update, stale preview, transaction rollback, and retry after a committed response is lost.
- PostgreSQL migration with existing rows and provider-specific concurrency/index behavior, separately from the fast SQLite suite.

Run `dotnet test Kiseki.slnx`. No test should call live Jiten or Google Drive.

## Delivery boundary and future Google Drive work

Implement normalization and migration first, then the Core planner/application service, then Web and Console integration with their regression tests. This is one coherent manual-update feature: migration alone does not make imports safe.

Later, Google Drive supplies a stream and verified source identity to this pipeline. Authentication, file discovery, scheduling, remote deletion handling, and sync UI remain a separate project. Keep transport-specific checkpoints outside the daily merge policy. This plan does not promise automatic matching across source renames without reliable identity, or automatic deletion from incomplete snapshots.

The main recommended product choices are: newer source revisions win even when totals decrease; missing dates are retained; legacy differences require a reviewed baseline; ambiguous matches require explicit selection. Confirm the daily-snapshot and revision semantics with real exports before coding these assumptions into the merge contract.
