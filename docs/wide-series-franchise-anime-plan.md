# Wide Series, Franchise Catalogue, and Anime Expansion Plan

**Status:** Deferred architecture proposal. Do not implement yet.

**Purpose:** Preserve enough context and architectural direction that a fresh chat can resume this work without reconstructing the design from conversation history.

## 1. Product Goal

Kiseki should know more than the media that has already produced a TTSU import or an immersion log.

The desired experience is:

- A series page can show every known installment, including volumes not yet imported from TTSU.
- The user can see overall series progress: what exists, what is tracked, and what is complete.
- A catalogue-only book discovered through Jiten can later be associated with a TTSU-tracked copy without creating a duplicate series installment.
- A franchise can group multiple related series across media: light novels, side stories, manga, anime, games, and so on.
- Anime can use the same broad hierarchy while treating seasons, cours, movies, OVAs, and specials as installments within an anime series.
- Cross-media progress remains honest. Kiseki must not combine characters, episodes, and hours into a meaningless single percentage.

Example:

```text
Franchise: Re:Zero
  Series: Re:Zero Main Light Novels [Book]
    Installment: Volume 1
    Installment: Volume 2
    ...
  Series: Re:Zero EX Novels [Book]
  Series: Re:Zero Anime [Anime]
  Series: Re:Zero Games [Game]
```

For Attack on Titan, the anime series might contain ordered installments such as Season 1, Season 2, Season 3 Part 1, Season 3 Part 2, Final Season Part 1, Final Season Part 2, and the Final Chapters specials. Those belong under one anime series, not as separate franchises.

## 2. Current Repository Context

At the time this document was written:

- Kiseki targets .NET 10 and contains Core, Web, Console, and Tests projects.
- The current hierarchy is `Franchise -> MediaSeries -> MediaWork -> ImmersionLog`.
- `MediaWork` is a tracked copy and aggregate root. The TTSU workflow explicitly supports creating multiple copies of the same book, each with its own history.
- `MediaWork` progress is currently book-specific and character-based.
- `MediaSeries` already has a media type, optional franchise, optional Jiten parent deck ID, and a collection of works.
- `Franchise` already has an optional Jiten anchor deck ID. Jiten itself does not expose a separate stable franchise ID; Kiseki anchors to a deck in the connected graph.
- `MediaType` currently includes Book, Anime, and Game.
- The Web Series pages are placeholders. Create/Edit do not yet provide a complete persisted workflow, and Details does not query or render actual series data.
- There is no Web Franchise section yet.
- The Library page currently queries `MediaWork` records and should remain a view of tracked copies, not a dump of every catalogue entry.
- The Jiten client can already fetch a deck with paginated subdecks and can fetch a franchise graph containing nodes and edges.
- TTSU/Jiten/Google Books automatic-import Stages 6A and 6B have been under active development. Several Stage 6B repair prompts exist in `docs/`. Audit and stabilize that work before beginning this plan.

No implementation for the design below existed merely because this plan was added.

## 3. Central Domain Decision: Installments and Tracked Copies Are Different

### 3.1 Recommended model

Add a canonical entity, provisionally named `MediaInstallment`, between `MediaSeries` and `MediaWork`:

```text
Franchise
  -> MediaSeries
       -> MediaInstallment
            -> zero or more MediaWork copies
                 -> ImmersionLog
```

Definitions:

- **MediaSeries**: one coherent progression line within a single media type.
- **MediaInstallment**: one canonical position in that progression, such as Book Volume 12, Anime Season 3 Part 2, a movie, an OVA, or a game.
- **MediaWork**: one user-tracked copy or edition with its own binding, source state, manual overrides, completion state, and history.
- **Franchise**: a cross-media umbrella containing related series.

This is preferable to representing catalogue-only items as special `MediaWork` rows. A series can contain one Volume 12 installment while the user has zero, one, or several tracked copies of Volume 12. Series progress counts the installment once, while personal history can preserve every copy.

### 3.2 Why not add only `CatalogOnly` to `MediaWork`?

That is a tempting smaller change, but it creates unresolved semantics:

- Multiple copies can make one volume appear multiple times in the series denominator.
- It becomes unclear which copy owns canonical Jiten metadata, ordering, and cover art.
- A catalogue row would pretend to be a tracked copy before any tracking exists.
- Converting the catalogue row into one tracked copy still leaves the problem of later rereads or alternate editions.
- Anime seasons are canonical entries even when the user has no corresponding tracking record.

A temporary `CatalogOnly` state should be used only if the user deliberately chooses a smaller MVP and accepts a later migration. The recommended implementation should create the canonical installment layer now.

### 3.3 Likely `MediaInstallment` responsibilities

Exact names should be confirmed during implementation, but the entity should be able to hold:

- `Id`
- `MediaSeriesId`
- canonical/display title
- stable explicit sort order
- optional display position or sequence label, for values such as `12`, `EX 2`, or `Season 3 Part 2`
- installment kind, such as Volume, Season, Cour, Movie, OVA, Special, or Game
- optional release date or release status
- whether it counts toward the default series-progress denominator
- provider identity and provenance needed to sync it safely
- canonical provider metadata such as a Jiten character total and provider cover
- zero or more linked `MediaWork` copies

Use explicit stored ordering. Do not re-parse titles on every query to decide where entries belong. Provider order and title parsing can seed the value, but the user must be able to correct it.

Leaving gaps in integer sort values (for example 100, 200, 300) can make insertion easier. A separate label should carry human-facing numbering rather than overloading the sort value.

### 3.4 Metadata ownership and overrides

Canonical metadata shared by all copies normally belongs on the installment. Copy-specific information remains on `MediaWork`.

The migration should initially preserve the existing Jiten and cover fields on `MediaWork`. Do not delete or reinterpret existing values in the first schema change. Establish an explicit effective-value policy before consolidation, for example:

1. copy-specific manual override;
2. canonical installment metadata;
3. legacy copy metadata during migration;
4. no value.

The exact precedence must account for the existing provider-neutral cover provenance work. A Jiten parent-deck cover is often a series fallback, not proof of an exact volume cover, and must not be relabeled as volume-specific.

## 4. Series Semantics and Progress

### 4.1 Series boundary

Use this rule:

> A series is one coherent progression line. A franchise groups related progression lines across adaptations and media.

Main novels, EX novels, a manga adaptation, and an anime adaptation should normally be separate series inside the same franchise. Do not use a franchise as a substitute for anime-season grouping.

### 4.2 Book-series summaries

A wide book-series page should distinguish at least:

- number of released, included installments;
- number completed;
- number with tracked copies;
- number known only from the catalogue;
- character progress where reliable totals exist;
- metadata coverage, such as totals known for 42 of 46 volumes;
- upcoming and excluded entries.

Example:

```text
12 / 46 volumes completed
14 / 46 volumes tracked
1.42M / 5.80M known characters read
Character totals known for 42 / 46 volumes
```

Completion and character-weighted progress answer different questions and should both be available. Missing character totals must not silently behave as zero-length volumes.

### 4.3 Multiple-copy rule

Series completion counts a canonical installment at most once.

If several `MediaWork` copies link to one installment:

- the installment is complete if at least one relevant copy is complete;
- a completion percentage should use one explicit policy, preferably the greatest valid progress among copies unless a primary copy is designated;
- lifetime immersion statistics may sum sessions across copies, because rereading is real activity;
- series denominator and canonical catalogue count must never increase because another copy was added.

This distinction needs unit tests because it is easy to regress.

### 4.4 Inclusion and release rules

`CountsTowardSeriesProgress` is orthogonal to tracking. A tracked side item can be excluded from a mainline denominator; an untracked canonical volume can be included.

Upcoming entries should remain visible but should normally be excluded from the default released-progress denominator. The page may offer scopes such as `Released mainline`, `All included`, and `Everything` rather than hiding the difference.

Provider data cannot always determine whether an entry is mainline, optional, a side story, or a duplicate edition. Auto-created values must remain reviewable and editable.

## 5. TTSU Reconciliation with Catalogue Entries

The later TTSU workflow should attach tracking to an existing canonical installment rather than create a competing series entry.

Desired flow:

1. Parse and normalize the TTSU source exactly as today.
2. Resolve or ask the user to choose the target `MediaInstallment`.
3. If that installment has no suitable tracked copy, create a `MediaWork` linked to it and attach the new `TtsuBinding`.
4. If it has exactly one unbound suitable copy, offer or perform the safe association according to the existing reviewed matching rules.
5. If it has multiple copies or an ambiguous binding, require explicit user selection.
6. Persist logs, bookmark state, and TTSU totals on the selected copy. Do not store copy-specific TTSU state on the installment.

Matching can use Jiten identity, normalized title, volume markers, and already-reviewed source hints, but ambiguity must continue to require explicit review. A title match alone must not merge two existing copies or rewrite an installment silently.

Creating or linking a TTSU-tracked copy should be idempotent under the existing import-operation receipt rules.

## 6. Jiten Catalogue Hydration and Synchronization

### 6.1 Hydrating a book series

For a `MediaSeries` linked to a Jiten parent deck:

1. Re-fetch the parent deck and all paginated subdecks server-side.
2. Build a preview of canonical installments to add or update.
3. Reconcile by provider identity first, then by explicit reviewed associations; do not rely only on title text.
4. Seed title, position, character total, and cover provenance where supported by the response.
5. Present uncertain ordering, duplicates, missing volume markers, and probable alternate editions for review.
6. Apply the approved changes in one transaction.

The operation must be idempotent. Refreshing the same Jiten series twice must not create duplicate installments.

### 6.2 Stable provider identity

Do not add a naive unique constraint to `MediaWork.JitenDeckId` or `MediaWork.JitenSubdeckId`: multiple copies may legitimately share the same Jiten identity.

Provider uniqueness belongs at the canonical installment layer. Design the key around how Jiten represents parent decks and subdecks, and cover both:

- a subdeck installment identified within a parent deck;
- a standalone deck used directly as an installment.

If multiple editions from the provider intentionally represent distinct canonical entries, the schema and sync preview must preserve that distinction.

### 6.3 Safe refresh behavior

Provider refresh must be additive and non-destructive by default:

- never delete a `MediaWork`, TTSU binding, or immersion log because an external result disappeared;
- never overwrite a manual correction silently;
- mark provider entries as missing/stale and show them for review if they vanish;
- retain provenance for every imported field;
- show a warning if the Jiten franchise graph reports truncation;
- accept cancellation tokens and map network failures to actionable UI messages.

Useful synchronization fields may include last successful sync time, provider revision/fingerprint if available, and provider-presence status. Do not invent a remote revision guarantee if the API does not offer one.

### 6.4 Cover behavior

The ongoing Stage 6B cover work remains authoritative for provider-neutral cover selection. Catalogue sync must integrate with it rather than create a second cover pipeline.

In particular:

- preserve the difference between an exact volume cover and a series-cover fallback;
- do not copy one Jiten parent cover to every volume as if it were exact;
- retain Google Books/Jiten/manual provenance;
- allow a canonical installment cover to be used by all its copies unless a copy has an explicit override;
- keep external cover lookups bounded, cached where appropriate, cancellable, and visible through useful progress UI.

## 7. Franchise Modeling and UI

### 7.1 Meaning of a franchise

A franchise is an organizational umbrella, not one numeric progression scale. It can include series measured in incompatible units.

Example franchise detail:

```text
Re:Zero Main Novels       12 / 46 volumes completed
Re:Zero EX Novels          1 / 6 volumes completed
Re:Zero Anime             38 / 61 episodes watched
Re:Zero Games              1 / 4 games completed
```

Do not produce a single percentage by adding characters, episodes, minutes, and games. The franchise page can summarize series counts and display each series' own metric.

### 7.2 Jiten franchise graph

Jiten's connected graph is discovery evidence, not a complete editorial classification. Connected nodes can be adaptations, spinoffs, alternate editions, or other related works.

Recommended workflow:

1. Create or link a franchise using an anchor deck.
2. Fetch the graph server-side.
3. Show proposed series and unassigned nodes in a preview.
4. Classify nodes by media type and parent/subdeck structure where possible.
5. Let the user approve series membership, split nodes into separate series, ignore nodes, and correct titles.
6. Persist the approved topology transactionally.

Do not automatically collapse every connected book node into one `MediaSeries`.

### 7.3 Franchise pages

Add real Web pages only after the underlying series/catalogue model is stable:

- franchise index;
- details grouped by media type and series;
- create/edit/link workflow;
- reviewed Jiten graph refresh;
- actions to move a series between franchises or leave it unassigned.

Deleting a franchise should continue to leave its series intact, consistent with the current set-null relationship.

## 8. Anime Design

### 8.1 Initial tracking granularity

For the first anime vertical slice, treat the following as `MediaInstallment` kinds:

- Season
- Cour or Part
- Movie
- OVA
- Special

Do not create one `MediaWork` per episode initially. A season/cour/special is the tracked unit, and episode progress lives within it. An episode entity can be added later only if Kiseki needs episode titles, per-episode dates, episode-level metadata, or detailed rewatch history.

Flatten irregular release structures into a deliberate display/watch order for the MVP. A later optional grouping layer can visually place multiple cours beneath one named season, but that hierarchy is not required to model Attack on Titan correctly.

### 8.2 Progress generalization

The current `MediaWork` API is character-specific. Before Anime ships, progress calculation must stop assuming that all media uses characters.

The implementation should preserve strongly meaningful source fields rather than prematurely replacing everything with an untyped `Value` plus `Unit` pair. A domain-level progress projection or strategy can expose a common UI shape while retaining typed storage:

- Books: characters read and volume completion.
- Anime: episodes watched, with optional minutes watched for activity statistics.
- Games: completion state and time played unless a better supported metric is introduced.

Questions to settle during the anime discovery stage:

- whether anime progress is manual first or imported from a service;
- what provider supplies canonical season/cour and episode totals;
- how rewatches are represented without distorting current completion;
- whether anime viewing sessions belong in a generalized activity log or a dedicated typed log;
- whether partially watched episodes are needed.

Do not retrofit TTSU's character fields to mean episodes.

### 8.3 Provider discovery spike

Before selecting an anime provider or extending Jiten usage:

- verify what Jiten exposes for anime and whether its hierarchy matches Kiseki's desired season/cour model;
- inspect rate limits, identifiers, images, episode totals, release status, and franchise relationships;
- compare only if necessary with other appropriate providers;
- document terms and attribution requirements;
- use stubbed API tests and never call live services from the test suite.

Attack on Titan should be used as a concrete acceptance case because its seasons, parts, specials, and naming irregularities will expose weak assumptions early.

## 9. Intended Web Experience

### 9.1 Series index

Replace the placeholder with a real read-only projection that supports the existing media tabs and shows, per series:

- title and media type;
- franchise, when assigned;
- completed versus released installments;
- tracked versus catalogue-only installments;
- metadata-coverage warning where relevant;
- a representative cover with existing fallback behavior.

Queries must use `AsNoTracking()`.

### 9.2 Series details

Render all canonical installments in stored order, including those with no `MediaWork`. Each row/card should make state obvious through concise labels such as:

- Catalogue only
- Tracked
- Completed
- Upcoming
- Excluded from progress
- Metadata needs review

Useful actions include:

- add an installment manually;
- refresh a Jiten-linked book series;
- link or create a tracked copy;
- inspect multiple copies;
- correct order and label;
- include/exclude an installment from the denominator;
- edit canonical metadata without destroying provider provenance.

Avoid rendering every uncertain provider result as a permanently expanded list. Use a bounded initial view, expandable alternatives, and focused review controls, following the lessons from Stage 6B's candidate-list UX.

### 9.3 Library boundary

The Library remains the user's tracked-copy view. Catalogue-only installments must not flood it.

If users later want to browse catalogue entries from Library, add an explicit filter or separate view. The default Library query should continue to return tracked `MediaWork` rows.

## 10. Migration and Compatibility Strategy

This change should be incremental and reversible at each stage.

Recommended migration outline:

1. Add `MediaInstallment` and its constraints without removing existing fields or relationships.
2. Add a nullable `MediaInstallmentId` to `MediaWork`.
3. Backfill a canonical installment for every existing tracked work so no work becomes orphaned.
4. Do not automatically merge two existing works merely because normalized titles match.
5. Where provider identity proves that existing works are copies of the same installment, propose or perform grouping only under a separately tested deterministic rule; ambiguous cases require review.
6. Keep the existing `MediaSeriesId` path temporarily if necessary for compatibility, then remove it only after every caller and migration path uses the installment relationship.
7. Copy canonical metadata rather than deleting legacy values during the first migration.
8. Add PostgreSQL EF migrations and update the model snapshot.
9. Extend `SqliteSchemaUpgrade` for existing local SQLite databases. Do not run PostgreSQL migrations against SQLite or recreate users' databases.

Every pre-existing row represents tracked user data. No migration may reinterpret it as an untracked catalogue-only object.

## 11. Phased Implementation

### Phase 0 — Audit and decisions

- Finish and audit the in-flight Stage 6B import/cover work.
- Establish a clean, tested baseline before schema work.
- Confirm the `MediaInstallment` name and boundary.
- Decide canonical-versus-copy metadata precedence.
- Decide ordering fields and installment kinds.
- Write a short domain decision record before migrations.

Exit criterion: model and migration behavior are agreed upon, including multiple-copy semantics.

### Phase 1 — Canonical installment foundation

- Add the entity, relationships, invariants, EF configuration, and migration.
- Backfill existing works conservatively.
- Update Core projections/services so tracked works resolve through an installment.
- Keep the existing user experience functioning.
- Add SQLite upgrade support and migration tests.

Exit criterion: every existing tracked work belongs to a canonical installment and no user data is lost or silently merged.

### Phase 2 — Wide book-series read model and UI

- Implement real Series Index and Details PageModels.
- Show catalogue/tracked/completed/upcoming/excluded states.
- Implement honest completion, character, and coverage summaries.
- Keep catalogue-only entries out of the default Library.
- Complete persisted manual Series create/edit behavior as required.

Exit criterion: a manually populated series can display more installments than the user has tracked.

### Phase 3 — Jiten book-series hydration

- Implement provider-neutral catalogue-sync abstractions in Core.
- Add reviewed preview and transactional application.
- Reuse existing Jiten pagination and cover provenance.
- Make refresh idempotent and non-destructive.
- Bound network concurrency and display per-series synchronization progress.

Exit criterion: a Jiten-linked light-novel series can populate all provider-known volumes without creating tracked copies or duplicate installments.

### Phase 4 — TTSU-to-installment reconciliation

- Add installment selection and matching to import review.
- Attach a new TTSU binding to the correct existing or newly created copy.
- Preserve explicit choice for ambiguity and multiple copies.
- Verify operation-receipt replay safety.

Exit criterion: importing a previously catalogue-only volume turns it into a tracked series installment without adding a second canonical volume.

### Phase 5 — Franchise views and reviewed graph sync

- Implement franchise pages and per-series summaries.
- Add reviewed Jiten graph discovery and classification.
- Preserve user corrections and handle truncated/stale graphs safely.
- Never calculate a combined cross-media completion percentage.

Exit criterion: one franchise can coherently show several book series and be ready to receive anime/game series.

### Phase 6 — Anime discovery and domain progress work

- Run the provider/data-model spike.
- Add anime installment kinds and a typed episode-progress design.
- Add manual anime series/installment tracking first if external integration is uncertain.
- Use Attack on Titan as an acceptance fixture.

Exit criterion: seasons, cours, movies, OVAs, and specials can be ordered and tracked without abusing franchise or book-character fields.

### Phase 7 — Anime integration and UX

- Integrate the selected provider through a Core client and stubbed tests.
- Add reviewed catalogue sync.
- Add watch-progress editing/import as separately scoped work.
- Surface anime series inside franchise pages using anime-appropriate metrics.

Exit criterion: Attack on Titan can be represented accurately and coexist with cross-media franchise entries.

## 12. Required Test Coverage

At minimum, add tests for:

- installment entity invariants and ordering;
- one installment with zero, one, and multiple `MediaWork` copies;
- multiple copies counting once in series totals;
- completed-copy and partial-copy progress selection;
- unknown character totals and metadata-coverage calculations;
- upcoming and excluded denominator behavior;
- idempotent Jiten series refresh;
- provider removal not deleting user data;
- exact provider identity reconciliation and ambiguous-title refusal;
- TTSU import attaching to an existing installment;
- TTSU ambiguity across multiple copies;
- franchise summaries retaining separate units;
- migration/backfill against SQLite and disposable local PostgreSQL where configured;
- Web PageModels using no-tracking read queries;
- no tests making live calls to Jiten, Google Books, or any anime provider.

Run the standard full verification before handing off any phase:

```powershell
dotnet test Kiseki.slnx
```

## 13. Non-Goals and Guardrails

This plan does not authorize:

- deleting or consolidating existing copies based only on title similarity;
- replacing the current TTSU review and receipt guarantees;
- treating a parent-deck Jiten cover as an exact volume cover;
- showing catalogue-only entries as though the user owns or has started them;
- calculating one cross-media franchise percentage;
- making every anime episode a first-class entity in the initial implementation;
- choosing or integrating an anime provider without a separate discovery review;
- starting this work while the current automatic-import branch is failing or unaudited.

## 14. Questions to Confirm Before Coding

A fresh chat should confirm these product choices with the user before Phase 1:

1. Is `MediaInstallment` acceptable terminology, or should the UI call these entries/items while retaining a technical entity name?
2. Should manually excluded side stories remain visible by default on the series page?
3. Should an installment count complete when any copy is complete, or should users designate a primary copy?
4. Should unreleased volumes be excluded from the headline denominator by default?
5. Should Jiten refresh require approval for every change, or may clearly new exact subdecks be added automatically after preview?
6. For anime, is manual episode-progress tracking an acceptable first release while provider integration is investigated?
7. Should manga become its own `MediaType`, or remain out of scope for the first catalogue/franchise slice?

Recommended defaults are: visible-but-excluded side stories, any completed copy satisfies installment completion, unreleased entries excluded from the headline denominator, all provider changes previewed, manual anime progress first, and manga deferred until explicitly scoped.

## 15. Fresh-Chat Handoff

When resuming this work in a new chat:

1. Read `AGENTS.md`.
2. Read `docs/automatic-import-implementation-plan.md` and the latest Stage 6A/6B audits and repair prompts.
3. Inspect the current working tree because the automatic-import implementation may still be uncommitted or in progress.
4. Run the full test suite and audit any divergence before designing migrations.
5. Read this document in full.
6. Discuss the questions in Section 14 with the user.
7. Prepare a narrowly scoped Antigravity prompt for Phase 0 or Phase 1 only. Do not ask it to implement the entire plan in one pass.

The architectural north star is:

> A canonical series installment describes what exists; a MediaWork describes the user's trackable copy; a franchise groups related series without pretending their progress units are interchangeable.
