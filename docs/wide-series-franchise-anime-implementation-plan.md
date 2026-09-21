# Wide series, franchises, and anime: implementation plan

Date: 2026-09-21. Baseline inspected: `8a12cb1`.

Status: planning only; no feature implementation authorized by this document. This translates [the architecture proposal](wide-series-franchise-anime-plan.md) into reviewable implementation batches and a Codex/Gemini ownership split. Proposed decisions below must be confirmed before schema work; they are not existing behavior.

## 1. Recommended delivery and ownership

Deliver wide book series first, then franchise organization, then manual anime tracking. Keep anime-provider integration behind a separate discovery decision. Do not hand either agent the entire expansion as one implementation prompt.

Codex should own the domain model, database changes, progress calculations, provider identity, transactional synchronization, TTSU reconciliation, and final integration. These changes cross existing invariants and require continuity across phases. Gemini can own substantial Razor UI work, PageModel presentation wiring, visual verification, and a bounded provider research spike once interfaces and acceptance fixtures are fixed. This division is about reducing coordination risk, not assuming one model is incapable of backend work.

| Batch | Deliverable | Primary implementer | Review/integration |
| --- | --- | --- | --- |
| 0 | Baseline audit and domain decision record | Codex | User confirms product decisions |
| 1A | Additive schema and conservative backfill | Codex | Migration tests and data-preservation review |
| 1B | Shared creation/assignment paths and compatibility | Codex | Web and Console regression checks |
| 2A | Book-series progress and query/command contracts | Codex | Deterministic acceptance fixtures |
| 2B | Real Series pages and manual catalogue editing | Gemini | Codex reviews domain usage and integrates |
| 3A | Jiten catalogue preview/apply service | Codex | Identity, concurrency, and replay tests |
| 3B | Series refresh review and progress UI | Gemini | Codex reviews server validation and integrates |
| 4A | TTSU installment/copy resolution | Codex | Existing import tests plus new concurrency cases |
| 4B | Import review controls for installment/copy choices | Gemini | Codex owns final import-flow integration |
| 5A | Franchise commands, summaries, graph planner | Codex | Topology and unit-separation tests |
| 5B | Franchise pages and graph review UI | Gemini | Codex integrates |
| 6A | Anime provider discovery report | Gemini | Codex evaluates fit; user selects scope/provider |
| 6B | Typed anime storage, progress, and manual commands | Codex | Domain and migration gates |
| 7A | Manual anime tracking UI | Gemini | Codex verifies the complete vertical slice |
| 7B | Optional provider client and reviewed sync | Codex; Gemini supplies UI | Separate approved provider contract |

No Gemini work has been executed as part of writing this plan. The assignments are proposed handoffs.

## 2. What the checkout actually contains

- `MediaSeries` and `Franchise` are persisted entities. Series Index/Details/Create/Edit PageModels remain placeholders; Create has an input model but no persisted POST workflow.
- `MediaWork.MediaSeriesId` is nullable. Console supports assigning/unassigning series and changing media type. Existing standalone works require explicit treatment in the new model.
- `ImmersionDbContext` configures series/franchise set-null deletion. `SqliteSchemaUpgrade` is a separate transactional upgrade path for older local databases.
- `TtsuImportService` creates tracked works inside a serializable transaction and verifies reviewed fingerprints before applying. It retains durable receipts across retries.
- `MediaWork.TotalCharacters` uses manual override, then TTSU edition total, then Jiten. Covers distinguish protected manual/legacy/TTSU values and Jiten, Google Books, and Open Library provenance.
- `IJitenApiClient` already exposes deck-detail and franchise-graph methods. Reuse and audit those implementations rather than adding a duplicate client.
- The proposal references `automatic-import-implementation-plan.md` and Stage 6A/6B audits/repair prompts that are absent from this checkout. The other current document is `antigravity-serpapi-amazon-cover-prompt.md`; it is a task prompt, not proof that its requested work is complete.
- The working tree was clean before this plan. Baseline `dotnet test Kiseki.slnx`: **674 passed, 4 skipped, 0 failed**. The skipped tests exercise PostgreSQL; this does not establish PostgreSQL migration/concurrency readiness.

Passing tests are a baseline, not a substitute for the import/cover audit required by the proposal. Audit current code and available history, recording unresolved issues without reconstructing missing documents as facts.

## 3. Decisions to lock in Batch 0

Write a short ADR containing the following recommended defaults and acceptance examples. Resolve objections before Batch 1A.

### Canonical ownership and standalone works

Use `MediaInstallment` internally and “installment” or the media-specific label in the UI. Give it an explicit media type, a nullable series relationship, ordered position, display label, kind, release state/date, inclusion flag, canonical metadata, provenance, and a concurrency token.

Allow an installment to be unassigned to a series. This is a deliberate clarification of the proposal: every work can receive an installment without inventing a series for standalone books. When assigned, installment and series media types must match; copy and installment types must also match.

Deleting a series leaves installments and copies intact and unassigned. Deleting an installment with copies is rejected; moving/reassigning copies is a separate explicit action. Deleting a franchise continues to set series membership to null. Media-type changes on linked records must use a validated command or require detachment, rather than leaving inconsistent types.

### Backfill versus provider uniqueness

Initially create one installment per existing work; never merge copies by title. Preserve each work ID, logs, bindings, receipts, overrides, and legacy metadata. Use a restart-safe mapping (for example, the work GUID as the initial installment GUID) and skip already linked records on retry.

Duplicated legacy provider identities make “one installment per work” incompatible with immediately claiming every identity uniquely. Resolve this explicitly: copy legacy metadata as provenance, but only establish a canonical provider identity when its mapping is unambiguous. Duplicate claims become review candidates. Reviewed consolidation moves copy relationships; it never combines or deletes their history.

Use a provider-identity mapping attached to installments, with a unique normalized identity key. Tentative Jiten forms are `deck:{deckId}` and `subdeck:{parentDeckId}:{subdeckId}`. Confirm parent/subdeck identity semantics against actual client fixtures before locking the key. A deck/subdeck alias needs an explicit reviewed association; identical titles do not prove equivalence. Different provider editions remain distinct unless reviewed otherwise.

### Metadata and progress

- Preserve copy totals in Batch 1. Target effective copy-count precedence is manual override > TTSU edition total > compatible canonical Jiten total > legacy copy Jiten total > unknown. Do not inherit a total from a different edition merely because titles match; preserve explicit zero overrides and treat nonpositive totals as unusable denominators.
- Protected copy covers remain protected. Preserve existing selected provider covers during migration; introduce canonical inheritance only through an explicit resolver with exact-cover versus parent-fallback provenance. Canonical refresh must not silently change every existing copy's cover.
- Store order independently of the display label; seed values 100 apart, with a stable ID tie-breaker. Reordering is a transaction, including any necessary renumbering. User-corrected order/title/inclusion must survive refresh.
- Any explicitly completed copy completes an installment. Otherwise use the greatest valid copy fraction, capped at 1; unknown copy totals do not become zero-length completed entries. Reading to 100% and an explicit completion flag remain distinguishable.
- Include released, included entries in the headline denominator. Upcoming/excluded entries stay visible with labels. Unknown release status stays visible and is reported separately from confirmed released counts; provide an explicit scope rather than silently guessing release dates.
- For known canonical book totals `C_i`, calculate normalized progress as `sum(C_i * p_i) / sum(C_i)`, where `p_i` is the selected copy fraction or 0 for an untracked installment. Label the numerator as estimated progress against known totals, not lifetime characters read: editions may differ and completion may be manual. Show unknown-progress/total coverage separately; no denominator means unavailable, not 0%.
- Lifetime activity remains the sum of actual sessions across copies. Franchise summaries retain each series' own units.
- Preview every provider change. Manual anime progress is the first anime release; manga and episode entities remain out of scope.

## 4. Implementation batches and exit gates

### Batch 0 — audit and contracts (Codex)

Audit `TtsuImportService`, import review tokens, cover resolution/protection, receipt replay, and the latest schema upgrades. Inventory all work creation, series assignment, Jiten relinking, and media-type mutation paths in Core, Web, and Console. Resolve failures relevant to this expansion in separate patches.

Produce the ADR, a writer/query inventory, representative fixtures, and the migration/backfill specification. Freeze command/result record shapes before assigning dependent UI work. Record which metadata fields are provider-owned, user-overridden, or legacy-unknown; store provider snapshots separately from manual corrections where needed.

Exit: agreed decisions, tested baseline, and no unresolved import correctness issue blocking schema changes. Optional cover-provider expansion need not be bundled into this feature.

### Batch 1A — schema and backfill (Codex)

Add `MediaInstallment`, provider identity/provenance storage, and nullable `MediaWork.MediaInstallmentId` while retaining `MediaSeriesId` and existing copy fields. Configure indexes, foreign keys, nonnegative totals, identity validation, and deletion behavior in `ImmersionDbContext`.

Generate PostgreSQL migrations with the repository-local tool; review generated SQL and snapshot. Extend `SqliteSchemaUpgrade` without recreating local databases or applying PostgreSQL migrations there. Run backfill before exposing catalogue operations. Treat concurrent application startup as a migration/backfill case, not just sequential replay.

Exit: fixtures with standalone works, duplicate identities, unknown covers, orphan logs, multiple copies, and mixed series retain all pre-existing data. Re-running upgrade/backfill creates no additional installments. Test both fresh creation and upgrade from old schema; `EnsureCreated` alone is insufficient.

### Batch 1B — compatible writers and readers (Codex)

Introduce shared Core commands for creating a tracked copy, assigning/moving an installment, and linking provider identity. Update Web manual/Jiten creation, Console add/edit/assignment, TTSU persistent creation, and convenience import paths. Every supported new-copy path must create or select an installment.

During compatibility, canonical installment membership is authoritative and commands keep legacy `MediaSeriesId` synchronized for every linked copy. Reject inconsistent assignments. Update queries to load installment/series plus logs where progress needs them; avoid navigation-loading-dependent totals.

Exit: Web, Console, Library details, Jiten linking/unlinking, and TTSU still work; no writer silently bypasses the new relationship. Keep removal of legacy columns/navigation in a later dedicated cleanup migration after all consumers are migrated. Do not promise old binaries are safe writers against the new schema.

### Batch 2A — manual catalogue and book read model (Codex)

Implement a pure Core progress calculator and catalogue commands, then immutable query results for Series Index/Details. Aggregate per installment before aggregating per series. Provide released/included, completed, tracked, catalogue-only, upcoming/excluded, known-total coverage, and unknown-progress counts.

Commands cover series create/edit and installment add/edit/order/include/exclude, copy creation/association, and reviewed consolidation. Query pages with `AsNoTracking()` and bounded projections; do not load an entire library's session history for every series card.

Exit: fixtures demonstrate one installment with zero/one/several copies, a manually complete copy without logs, different edition lengths, unknown totals, and upcoming/excluded entries. Multiple copies never inflate the denominator.

### Batch 2B — Series UI (Gemini)

Replace `Kiseki.Web/Pages/Series` placeholders using the approved services and records. Add immutable Web view models and focused shared partials as needed. Implement persisted create/edit, ordered installment rows, badges, metadata coverage, copy selection, and manual catalogue controls. Keep default Library queries restricted to `MediaWork`.

Reuse the existing dark design, CSS grid conventions, cover fallback, lazy loading, `referrerpolicy`, accessible progress markup, antiforgery, validation, and Post/Redirect/Get notices. No progress formulas in Razor/PageModels. Include empty, missing, invalid, and concurrency-conflict states, plus PageModel tests and desktop/mobile browser verification.

Exit: a manually populated series visibly contains more installments than tracked copies and can be edited without provider access. This is the first useful release checkpoint.

### Batches 3A/3B — reviewed Jiten catalogue sync (Codex/Gemini)

Codex implements a pure reconciliation planner and transactional application service in Core; Gemini renders its preview and result contracts. Fetch all pages outside the database transaction. Hold an authoritative server-side snapshot with a fingerprint, target versions, expiry, and approved choices; never trust posted titles/counts/covers.

Preview additions, provider-field changes, manual conflicts, uncertain ordering, duplicate identities, and disappeared entries. Incomplete pagination or failed fetches cannot establish that entries disappeared. Mark missing/stale only after a complete successful snapshot; preserve user data and corrections.

At confirmation, revalidate provider data as appropriate and compare local versions; changed evidence requires review again. Apply identity claims and all accepted changes atomically, with a durable operation receipt and database uniqueness handling for concurrent refreshes. Reuse existing pagination and cover services. Keep enrichment bounded and cancellable; optional image failure must not invalidate otherwise valid catalogue metadata.

Exit: repeated and concurrent refreshes create no duplicates; stale previews cannot overwrite corrections; retries after lost responses return the committed result. Catalogue hydration creates zero tracked copies. Gemini's UI shows meaningful fetch/review/apply states and actionable errors.

### Batches 4A/4B — TTSU reconciliation (Codex/Gemini)

Codex extends existing import request/plan/fingerprint contracts with installment selection and copy intent: existing copy, new copy under an installment, or new installment plus copy. Persisted source hints and provider identity precede title suggestions; ambiguous matches require review. A suitable unbound copy can be offered but must not absorb another copy's binding/history silently.

Include installment identity/version, copy assignment, binding state, and all choices in review validation. Catalogue selection, copy creation, logs/bookmark updates, binding, and receipt must commit in the existing atomic operation. Recheck races where another import binds the suggested copy or sync changes the target. Keep TTSU totals and bookmark state on the copy.

Gemini adds compact selection controls to the existing import flow after the backend contract is tested. Codex owns integration of import PageModels/batch-store changes; avoid simultaneous edits to those files.

Exit: importing a catalogue-only volume creates a linked copy without a second installment; importing a reread preserves separate history; ambiguous choices, expired previews, and receipt replay behave correctly.

### Batches 5A/5B — franchises (Codex/Gemini)

First deliver manual franchise create/edit, series membership/move/unassign, and details with per-series metrics. Then implement graph discovery: Core proposes nodes/classification and the UI captures approval, splitting, ignoring, and unresolved nodes. Persist approved topology transactionally with stale-review protection.

Graph connectivity is evidence, not permission to merge series. Keep a media classification unknown when unsupported; do not infer an anime watch order from arbitrary graph traversal. Preserve ignored nodes/manual decisions across refreshes and surface truncation prominently.

Exit: main novels, side novels, and another media series coexist without a cross-media percentage; deleting a franchise preserves series; truncated or stale graph results cannot destructively rewrite membership. Wide books plus manual franchises form the second release checkpoint.

### Batches 6A/6B — anime discovery and typed domain (Gemini/Codex)

Gemini researches current official provider documentation and representative payloads: stable IDs, seasons/cours/specials, ordering, episode totals, release dates, images, pagination, rate limits, terms, and attribution. Report sourced findings and unresolved gaps; do not select/install a provider or implement a client in this task. This can overlap book UI work once a bounded research brief exists.

Codex designs typed episode progress and the manual workflow. Recommended MVP: integer episodes watched per tracked copy, optional viewing minutes recorded as activity, explicit completion, and a separate copy for a rewatch. Choose typed activity storage in the ADR before migrating; do not reinterpret `CharactersRead`. Corrections to current episode progress must not fabricate or double-count activity.

Expose a common progress projection with media-specific labels/units and nullable totals. Preserve existing book behavior and avoid inventing game percentages. Add episode invariants, totals/override ownership, migrations, SQLite upgrade, and Core commands. Movies/specials may use one episode only when explicitly represented that way.

Exit: an explicitly authored Attack on Titan fixture represents seasons, parts, and final specials in order, without duplicate finale entries or episode-level entities. Unknown counts, completion overrides, corrections, and rewatches are tested. This fixture is an acceptance example, not an assertion that any provider already supplies that topology.

### Batches 7A/7B — anime delivery (Gemini/Codex)

Gemini implements manual anime creation, episode-progress editing, and series/franchise presentation on the stable typed contract. Ship manual anime independently if provider discovery is inconclusive.

Only after the provider decision, Codex adds its Core client, normalization, identity mappings, and reviewed sync using stubbed HTTP tests. Gemini adds the provider-specific review surface. Watch-history import remains separately scoped; catalogue integration does not automatically authorize it.

Exit: manual anime is usable end to end alongside books, and optional provider refresh preserves manual topology/progress and satisfies the discovery constraints.

## 5. Agent handoff protocol

Each Gemini task should receive the base commit, this plan, `AGENTS.md`, one batch's scope, immutable contracts, seeded fixtures, exact allowed files, and acceptance checks. Suggested first coding handoff is **Batch 2B only**, after 2A is integrated.

Example task boundary:

> Implement Series pages against the supplied catalogue query/command contracts. Preserve the existing Razor/CSS/vanilla-JavaScript stack. Render the supplied states and add PageModel coverage. Do not modify Core entities, migrations, progress policy, import services, or provider clients. If a contract cannot support an acceptance case, report the gap before inventing domain behavior. Return the patch, test results, browser evidence, and remaining issues.

Use separate branches/worktrees for actual parallel implementation. Only one agent owns a migration/model snapshot at a time. Assign shared `site.css`, `site.js`, navigation, and import PageModels explicitly so two tasks do not rewrite them concurrently. Gemini may build against approved fixtures while Codex implements services, but integration waits for real contracts; mocks are not final verification.

Codex reviews each handoff for domain calculations outside Core, provider trust boundaries, stale-review handling, missing input validation, accidental copy creation, and query loading/performance. Run the complete suite after integration, not just on each branch. Return contract changes to the owner rather than silently revising the schema from a UI task.

## 6. Verification and release discipline

For every implementation batch run `dotnet test Kiseki.slnx`. Add tests where behavior changes, emphasizing data preservation and failure/concurrency boundaries rather than tests that merely reproduce rendering code.

For schema batches also generate/review the migration and updated snapshot with:

```powershell
dotnet tool run dotnet-ef migrations add <MigrationName> --project Kiseki.Core --startup-project Kiseki.Core
```

Exercise disposable SQLite files containing the old schema and disposable local PostgreSQL through `KISEKI_TEST_POSTGRES`. Require actual PostgreSQL execution before a PostgreSQL schema release; record skipped integration tests honestly. Verify old row IDs/counts and field values, repeat startup/upgrade, and test interrupted/concurrent work. Never target the user's real database in tests.

UI gates cover real query/command behavior, empty and error states, keyboard access, responsive layout, console errors, and a browser flow from catalogue-only installment through tracking to series summary. External API tests use stubs; live provider research stays outside the suite.

Take a database backup before production upgrades. Prefer additive fixes and disabling new entry points over destructive rollback once catalogue data exists. Restoring a backup loses later writes and is not an automatic rollback strategy. Defer removal of legacy fields until all consumers are migrated and a separate compatibility review permits it.

The next actionable implementation task is Batch 0: audit the current import baseline and finalize the ADR. No schema, catalogue hydration, or anime integration should start merely because this plan has been written.
