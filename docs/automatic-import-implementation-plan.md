# Automatic TTSU metadata enrichment implementation plan

Status: proposed. This plan is based on the current repository. `AUTOMATIC_IMPORT_DESIGN.md` is product direction, not an implementation contract.

## Objective

Add an optional, confidence-based Jiten suggestion to the existing TTSU preview. A user should be able to import reading history exactly as they can today, while high-confidence new books can also receive a verified volume-level Jiten link and the cover already supplied by Jiten.

Reading history remains the primary operation. Jiten outages, uncertain matches, and missing covers must leave a book importable without metadata.

The first release does **not** scrape Amazon Japan or BookWalker. Kiseki currently has no approved client, identifier mapping, usage policy, image inspection pipeline, or storage model for those sources. The later cover stage below defines the prerequisites instead of guessing at them.

## What exists now

The implementation must extend these paths rather than create a second import workflow.

### TTSU upload, preview, and commit

- `Kiseki.Web/Pages/Import/Ttsu.cshtml.cs` parses uploaded statistics and progress files, combines files, stores the parsed batch, and calls `PopulateAsync` to build the review.
- `Kiseki.Web/Services/TtsuImportBatchStore.cs` holds parsed batches in memory for 30 minutes. A restart or another unshared Web instance can lose an uncommitted preview.
- `Kiseki.Web/Models/TtsuImportBatch.cs` stores the server-owned books and reviewed-plan fingerprints. The browser receives book keys and review tokens, not the source objects.
- `Kiseki.Core/Services/TtsuImportService.cs` owns source matching, read-only previews, serializable commit, collision handling, and durable operation receipts.
- `TtsuImportService.ApplyAsync` recomputes every plan and compares its fingerprint before writing. Replayed operation IDs return the existing receipt.
- A changed target, conflict choice, or progress choice must be refreshed before it can be committed. Metadata choices must preserve this behavior.
- `TtsuBinding` is the durable association between a TTSU source and a `MediaWork`. A confirmed binding takes precedence over title matching.

### Jiten linking

- `IJitenApiClient` supports book search, paginated deck details, and franchise retrieval.
- `LinkJitenModel` searches Jiten, loads subdecks, and re-fetches deck details on POST. It never trusts posted titles, character counts, or covers.
- `JitenMediaSelection` translates a fresh Jiten DTO into domain values and applies the selection to `MediaWork`.
- `MediaWork.LinkToJitenDeck` and `LinkToJitenSubdeck` validate IDs, character counts, and HTTPS cover URLs.
- The current Web and Console interfaces still allow a whole parent deck with children to be linked to one work. That conflicts with the volume-level safety rule and must be fixed before auto-linking.
- A subdeck without a cover currently inherits the parent cover in `JitenMediaSelection.FromSubdeck`, but the model does not retain whether the cover is volume-specific or a series fallback.

### Covers

- The single persisted field is named `MediaWork.JitenCoverUrl`, although the details editor can put any valid HTTPS image URL into it.
- `DetailsModel.OnPostUpdateCoverUrlAsync` and `MediaWork.UpdateCoverUrl` validate URL shape and length only. They do not verify response content, dimensions, aspect ratio, language, or edition.
- `cover-fallback.js` swaps a broken image for the media glyph in the browser. It is display recovery, not server-side cover validation.
- Existing rows do not say whether a cover came from Jiten or was customized by the user. Automatic enrichment must therefore treat any existing cover as protected.

### Important consequences

1. Enrichment belongs in the existing server-held preview batch.
2. The browser may post only an opaque candidate choice. The server must resolve its IDs against the batch and re-fetch Jiten before commit.
3. Metadata must be applied inside the same database transaction as the accepted TTSU plan, but no network request should run inside that transaction.
4. Existing Jiten links and existing covers must never be overwritten by automatic enrichment.
5. A TTSU bookmark-derived total is useful match evidence. The sum of daily reading is not a book-length estimate and must never be used as one.

## Target design

Use one additive path:

```text
uploaded files
  -> existing TTSU parsing and normalization
  -> existing server-held batch
  -> existing TTSU target/merge preview
  -> optional bounded Jiten candidate discovery
  -> preview stores candidates and opaque candidate keys
  -> user refreshes reviewed choices
  -> server re-fetches chosen Jiten deck/subdeck
  -> existing TTSU transaction creates/updates work and conditionally applies metadata
  -> durable receipt records both reading and metadata results
```

Keep these responsibilities separate:

| Responsibility | Owner |
| --- | --- |
| TTSU parsing, normalization, merge policy, and source binding | Existing Core TTSU services |
| Deterministic title and volume parsing | New pure Core parser |
| Jiten candidate discovery, hierarchy expansion, scoring, and failure classification | New Core match service using `IJitenApiClient` |
| Pending candidates and opaque candidate keys | Existing Web batch store/models |
| Target choice, match badges, and metadata opt-in | Existing TTSU Razor Page |
| Fresh selection verification | Shared Core Jiten resolver used by manual and automatic flows |
| Transactional application and overwrite guards | `TtsuImportService.ApplyAsync` plus `MediaWork` methods |

Do not add a second controller, a second import transaction, or a database table for pending candidates. Pending candidates have the same 30-minute lifetime as the preview that owns them.

## Matching contract

The match result must be deterministic and explainable. Store evidence, not just a percentage.

### Parsed input

Create a pure parser that returns:

- the original title;
- an NFKC-normalized comparison title;
- a base/search title with only recognized release noise and the recognized terminal volume marker removed;
- an optional structured volume marker;
- a flag for fractional, `EX`, side-story, short-story, arc, or otherwise special numbering;
- human-readable parsing notes.

Start with a small, tested grammar:

- file suffixes such as `.epub`, `.html`, and `.txt`;
- leading bracketed release/publisher tags;
- Arabic terminal volume numbers, including `01` and `4.5`;
- explicit `vol`, `volume`, `巻`, and `第…巻` forms;
- conservative Roman numerals only when they occur in an explicit terminal volume context;
- `上`, `中`, and `下` only when clearly used as a terminal volume marker;
- explicit `Ep.`, `EX`, short-story, and school-year/arc markers, always marked special.

Do not remove arbitrary bracketed subtitles, internal numbers, or a lone Roman letter merely because it could be a volume. False extraction is more damaging than returning “no volume marker.”

### Candidate discovery

For each distinct normalized search title in a batch:

1. Call `SearchBooksAsync` once and cache that result for the duration of the enrichment operation.
2. For a result with child decks, call `GetDeckDetailAsync` once and score only its subdecks.
3. For a result whose `ParentDeckId` indicates it is already a child, load/verify its parent detail before presenting it.
4. A standalone deck with no children can be scored as a work candidate.
5. A parent deck with children is never a work candidate.
6. De-duplicate candidates by `(parentDeckId, subdeckId)` before scoring.

Only Jiten's book search (`mediaType=4`, already enforced by `JitenApiClient`) is in scope. Do not invent manga-versus-light-novel classification from generic character-count ranges; the current domain and DTOs do not carry a reliable subtype.

### Score and confidence

Use the proposal's three evidence families, but omit “chronological sequence” until the API supplies a reliable ordering contract:

| Evidence | Points | Rule |
| --- | ---: | --- |
| Base title | 0–40 | Exact normalized match across original, English, or romaji is 40. Conservative token/containment matches may receive less but cannot produce high confidence by themselves. |
| Volume identity | 0–35 | Exact structured volume match is 35. Conflicting explicit volumes disqualify the candidate. Both sides having no volume may count only for a verified standalone deck. |
| TTSU total sanity | 0–20 | Use only an authoritative inferred TTSU total. Within 15% is 20; 15–30% may receive 10; otherwise 0. Missing totals are neutral. |

Initial categories:

- **High**: score at least 85, no hard disqualifier, and at least 10 points above the runner-up.
- **Review**: score 60–84, a close runner-up, or otherwise useful but incomplete evidence.
- **None**: below 60 or no safe candidate.

Additional hard rules:

- Cap special/fractional/side-story input at Review, regardless of score.
- Never classify a childless-looking search row as standalone until fresh deck detail confirms the hierarchy during selection resolution.
- Never use daily characters read as character-count evidence.
- Never allow a cover to increase metadata match confidence. A plausible image does not establish volume identity.
- Include an evidence list such as “exact original base title,” “volume 3 matched,” and “TTSU total differs by 6.2%” for the preview and tests.

Thresholds are constants in one scorer, not scattered through PageModels. They may be tuned after fixture-based evaluation without changing the flow.

## Staged implementation

Each stage should compile and pass tests before the next stage begins. Gemini Flash should implement one stage at a time and should not pre-create types for later stages unless the current stage uses them.

### Stage 0 — Make the existing Jiten selection safe and reusable

Goal: establish one authoritative way to resolve a deck/subdeck, and prevent parent-series links from all work-level linking flows.

Implementation:

1. Add a Core resolver, for example `Kiseki.Core/Services/JitenSelectionResolver.cs`.
   - Input: parent deck ID, optional subdeck ID, and cancellation token.
   - Fetch the requested detail through `IJitenApiClient`.
   - Verify that the requested parent is the returned `MainDeck` or `ParentDeck`.
   - If a subdeck ID is supplied, require it to exist in the fresh `SubDecks` collection.
   - If no subdeck ID is supplied, allow the parent only when it has no children and the fresh response has no subdecks.
   - Return a typed success/failure result; do not use `null` to represent every failure.
2. Refactor `LinkJitenModel.OnPostLinkAsync` to use the resolver while preserving its friendly HTTP/JSON/timeout messages.
3. In `LinkJiten.cshtml`, do not render “Link entire deck” for results with children. Render only “Choose a subdeck.”
4. In `Kiseki.Console/Screens/JitenLinkScreen.cs`, remove “Link the entire deck” when the selected deck has children.
5. Extend `JitenMediaSelection` with cover evidence: `Specific`, `ParentFallback`, or `None`. Continue allowing the parent fallback, but label it accurately in Web/Console output.

Tests:

- Resolver accepts a fresh standalone deck.
- Resolver accepts a subdeck contained by the fresh parent response.
- Resolver rejects a missing subdeck, mismatched parent, non-positive IDs, and a parent that has children.
- Manual Web linking still refetches and persists authoritative title/count/cover values.
- Web markup/action tests and Console selection tests establish that a parent with children cannot be linked to one work.
- Existing parent-cover fallback test now also asserts `ParentFallback`.

Done when: no supported work-level flow can newly link a series parent that has child volumes.

### Stage 1 — Add the pure parser and scorer

Goal: implement matching decisions without changing Web behavior or making network calls in unit tests.

Suggested files:

```text
Kiseki.Core/Services/Metadata/MediaTitleParser.cs
Kiseki.Core/Services/Metadata/JitenCandidateScorer.cs
Kiseki.Core/Models/Metadata/ParsedMediaTitle.cs
Kiseki.Core/Models/Metadata/JitenMatchCandidate.cs
Kiseki.Core/Models/Metadata/JitenMatchResult.cs
Kiseki.Tests/MediaTitleParserTests.cs
Kiseki.Tests/JitenCandidateScorerTests.cs
```

Implementation rules:

- Keep parsing and scoring free of EF Core, HTTP, Razor, and current culture.
- Normalize with `NormalizationForm.FormKC` and invariant comparisons.
- Represent a volume marker structurally; do not pass an unvalidated string from parser to scorer.
- Score all supplied title variants and retain which variant produced the evidence.
- Sort candidates deterministically by score, title, parent ID, then subdeck ID.
- Return the runner-up margin and evidence with the category.

Tests should cover Japanese, English, and romaji titles; zero-padded numbers; explicit Roman volumes; `4.5`; `EX`; `上/中/下`; unrelated internal numbers; conflicting volumes; no TTSU total; exact and divergent totals; ties; and deterministic ordering.

Done when: a table-driven test suite documents every supported title form and all confidence boundary conditions.

### Stage 2 — Add bounded Jiten candidate discovery

Goal: turn a TTSU title and optional inferred total into ranked, server-owned candidates without affecting import success.

Suggested files:

```text
Kiseki.Core/Services/Metadata/IJitenMatchService.cs
Kiseki.Core/Services/Metadata/JitenMatchService.cs
Kiseki.Tests/JitenMatchServiceTests.cs
```

Implementation:

1. Compose `MediaTitleParser`, `IJitenApiClient`, the Stage 0 resolver/hierarchy rules, and `JitenCandidateScorer`.
2. Collapse identical base-title queries within one batch.
3. Bound outbound work with one `SemaphoreSlim` shared by the enrichment operation and configured from options; start with three concurrent Jiten operations. Do not create one semaphore per book.
4. Add a small per-enrichment cache for searches and deck details. Do not introduce a global stale metadata cache in the first release.
5. Use a bounded retry policy for HTTP 429 and transient 5xx responses only. `IJitenApiClient` currently reduces failed responses to `HttpRequestException`, which loses the `Retry-After` header. Add a small typed Jiten HTTP exception carrying status and optional retry delay (and keep it compatible with the existing displayable-failure handling), or put the retry in `JitenApiClient` while the response headers are available. Respect `Retry-After` when present; otherwise use at most two retries with jitter. Do not retry cancellation, JSON/schema errors, or ordinary 4xx responses.
6. Return `Unavailable`/`RateLimited`/`NoCandidates` as data for one book. Do not throw those failures through the TTSU preview.
7. Use an overall enrichment time budget supplied by the Web caller. When that internal budget expires, mark unfinished books unavailable and render the ordinary reading preview. If the ASP.NET request cancellation token itself is cancelled, propagate it rather than converting it to an availability result.

The authoritative inferred total must come from TTSU progress normalization (exact ratio or completion-adjusted). Rounded legacy percentage strings and daily reading totals are not eligible evidence.

Tests use stub `HttpMessageHandler`/`IJitenApiClient` instances only. Cover pagination, query de-duplication, detail de-duplication, concurrency bounds, cancellation, 429, malformed responses, parent exclusion, child expansion, and partial batch failure.

Done when: a 100-book synthetic batch cannot exceed the configured concurrency and one failed Jiten request does not discard other results.

### Stage 3 — Attach enrichment to the existing preview

Goal: show suggestions in the TTSU review without writing metadata.

Model changes:

- Add `AutoMatchMetadata` to the upload form.
- Extend `TtsuImportBatchBook` with an optional, server-owned enrichment result.
- Give each displayed candidate a random opaque `Guid` key owned by that batch/book. Keep Jiten IDs, counts, titles, and covers server-side.
- Extend `TtsuBookPreviewViewModel` with enrichment status, evidence, candidates, selected candidate key, cover kind, and an apply-eligibility message.
- Extend `TtsuBookSelectionInput` with an optional candidate key. Do not add posted title/count/cover fields.

Page flow:

1. Parse and store the TTSU batch exactly as today.
2. If the option is enabled, enrich each stored book once within the request budget and store the result in the batch.
3. `PopulateAsync` reuses the stored result. `Refresh review` must not repeat searches.
4. Preselect the top candidate only for High results.
5. Review/ambiguous results default to “Import without Jiten metadata,” while showing ranked alternatives.
6. Unavailable results show a warning on that book and remain selected for reading import.
7. A previously bound target, an already Jiten-linked target, or any target with an existing cover is marked protected. The suggestion may be shown, but automatic application must be disabled.
8. A target change recomputes eligibility during `PopulateAsync`.

UI changes in `Ttsu.cshtml`:

- Add the optional checkbox near the folder picker.
- Add `Auto-matched`, `Needs review`, `No safe match`, and `Jiten unavailable` badges.
- Show the candidate title, deck/subdeck IDs, character count, thumbnail, cover-kind label, score, and short evidence list.
- Offer “Import without Jiten metadata” and the server-provided candidate choices.
- Keep the existing TTSU target, daily conflict, progress conflict, orphan, refresh, and partial-selection controls intact.
- Do not build inline arbitrary Jiten search in this stage. The existing post-import `LinkJiten` page remains the escape hatch.

Persist the checkbox in browser local storage with a small dedicated JS component. The posted checkbox remains the server input; local storage is preference convenience, not trusted state. Initially default it off for users with no stored preference.

Review integrity:

- Extend `TtsuReviewedPlan` to include the reviewed candidate key (including an explicit “none”).
- Confirmation with a changed candidate but an old review token must refresh, just as target/day/progress changes do today.
- A candidate key must belong to the same batch book. Reject cross-book or unknown keys.

Tests extend `TtsuImportPageTests` and use a stub match service. Assert no database writes during preview, stable candidates across refresh, opaque-key validation, protected existing metadata, high-confidence defaulting, ambiguous no-link defaulting, local failure isolation, and normal operation with the option disabled.

Done when: the complete enriched preview is read-only and the old preview behaves identically when enrichment is off.

### Stage 4 — Apply freshly verified metadata in the existing transaction

Goal: commit reading data and an eligible metadata choice safely, with reading import succeeding if Jiten cannot be verified.

Before opening the database transaction:

1. Resolve every posted candidate key through the server-held batch.
2. Re-fetch the chosen deck/subdeck with the Stage 0 resolver. Never copy previewed or posted metadata into a work.
3. Convert successfully resolved choices to fresh `JitenMediaSelection` instances.
4. Convert rate limits, timeouts, deleted subdecks, and hierarchy changes to a per-book metadata skip. Continue with the reading request.

Core changes:

- Add an optional fresh `JitenMediaSelection` to `TtsuImportRequest`. Keep the parameter optional so Console and existing tests remain source-compatible where practical.
- In `TtsuImportService.ApplyAsync`, apply the selection after loading/creating the tracked work and before `SaveChangesAsync`.
- Always keep the TTSU title (`JitenTitleChoice.KeepCurrent`) during automatic linking.
- For a new work, apply a valid selection.
- For an existing work, apply only when both `HasJitenLink` is false and `JitenCoverUrl` is null at commit time.
- If another request added a link or cover since preview, skip metadata rather than overwrite it or fail reading import.
- Never modify `ManualCharacterCountOverride`, `TtsuCharacterCount`, completion, series/franchise assignment, or existing logs while applying metadata.
- Continue relying on `TotalCharacters = Manual ?? TTSU ?? Jiten`; do not copy Jiten counts into TTSU/manual fields.

Receipt changes:

- Add durable `MetadataLinks` and `MetadataSkips` counts to `TtsuImportReceipt` so replayed confirmations return an accurate result.
- Generate an EF migration and update `ImmersionDbContextModelSnapshot`.
- Add the same defaulted columns to `SqliteSchemaUpgrade` for existing local databases created outside the PostgreSQL migration path.
- Include the counts in the Post/Redirect/Get notice.

Do not perform Jiten HTTP calls from inside `ExecuteAsync`, the serializable transaction, or an EF execution-strategy retry. Database retries may repeat local application of the already-fetched immutable selection, but must not repeat external calls.

Tests:

- New work receives the fresh subdeck ID, Jiten count, and cover.
- Fresh data wins over stale preview display values.
- Parent-with-children and disappeared subdeck are skipped while reading imports.
- Existing link, existing manual/legacy cover, and a concurrent cover/link change are preserved.
- Manual and TTSU character counts keep precedence.
- Mixed batches can link one book, skip another, and import all selected reading data.
- Replayed operation IDs return the same metadata counts and do not call Jiten again.
- A database rollback leaves both reading and metadata unchanged.
- SQLite schema upgrade and PostgreSQL migration tests cover the new receipt columns.

Done when: a metadata failure can never roll back otherwise valid selected reading imports, and automatic metadata can never overwrite an existing link or cover.

### Stage 5 — Calibrate and enable the hybrid default

Goal: validate the confidence rules before making auto-selection the common path.

1. Add sanitized JSON fixtures representing standalone books, ordinary numbered series, Japanese counters, ambiguous adaptations, `4.5`, `EX`, side stories, missing totals, and misleading title numbers.
2. Record the expected candidate and confidence for each fixture in table-driven tests.
3. Manually run multi-book folders with Jiten success, rate limiting, timeout, expired preview, target changes, and mixed high/ambiguous matches.
4. Confirm every High fixture has the correct volume and a sufficient runner-up margin. Lower the category to Review when uncertain; do not weaken hard gates to improve the match rate.
5. Only after that review, change the no-preference UI default to enabled if desired. Keep the checkbox and per-book opt-out permanently.

Done when: the fixture set has zero known false High matches. A low automatic match rate is acceptable; a false volume-level link is not.

### Stage 6 — Add non-Jiten high-resolution covers only after provider approval

This is a separate delivery, not part of the first automatic metadata release.

Prerequisites that require an explicit product decision:

- an approved Amazon JP, BookWalker, or other provider API/integration rather than HTML scraping;
- permitted hotlinking or a lawful local caching strategy;
- a stable Japanese-edition identifier, preferably ISBN/product ID, returned or mapped from Jiten/provider metadata;
- response-size, MIME-type, timeout, and redirect limits;
- an image dimension/aspect inspection implementation that works in the Linux deployment image;
- retention and refresh policy.

Before adding providers, generalize cover persistence:

- Introduce a generic cover URL in the domain while preserving existing data.
- Store source/provenance and whether the value is a user override.
- Migrate existing non-null `JitenCoverUrl` values as `LegacyUnknown` and treat them as protected.
- Make the manual cover editor set `UserOverride`.
- Decide whether removing a Jiten link should retain a user cover; do not preserve the current accidental coupling without review.

Provider resolution rules:

1. Resolve covers only after the exact Jiten work candidate is known.
2. Require Japanese-edition identity independently of title similarity.
3. Validate HTTPS, redirect target, content type, byte limit, dimensions, and aspect ratio server-side.
4. Prefer a verified volume-specific image. Label a parent image as `SeriesFallback`; never describe it as the volume cover.
5. Never substitute an adjacent volume.
6. Cover uncertainty never reduces metadata-link confidence and never blocks reading import.
7. Never overwrite a user override or `LegacyUnknown` cover.

Tests must stub every provider and image response. Automated tests must not call live retailer, Jiten, or CDN endpoints.

## Explicit non-goals for the first release

- No Amazon/BookWalker scraping.
- No background scheduler, Google Drive sync, or automatic re-enrichment after import.
- No new behavior in the scaffolded `/Import/Jiten` page.
- No ML/LLM matching and no opaque fuzzy score.
- No automatic title replacement, series/franchise creation, or Jiten franchise graph mutation.
- No overwriting existing metadata, even when the new score is higher.
- No persistence of pending candidates beyond the existing preview lifetime.
- No live-network tests.

## Implementation guardrails for Gemini Flash 2.8 High

1. Implement stages in order. At the start of each stage, re-open the named current files because earlier stages will have changed their signatures.
2. Keep changes narrow. Do not rename the TTSU planner, replace the batch store, or redesign the import UI while adding metadata.
3. Preserve cancellation tokens on every async EF and HTTP call.
4. Use `AsNoTracking()` for preview reads and tracked entities only in mutations.
5. Treat all posted IDs, enum values, GUIDs, and candidate keys as untrusted.
6. Never accept posted Jiten titles, counts, cover URLs, or confidence scores.
7. Never run external HTTP inside a database transaction or execution-strategy callback.
8. Do not catch request cancellation as a recoverable Jiten failure.
9. Prefer typed result records and short pure methods over flags spread across Razor code.
10. Add or update tests in the same stage as behavior. Do not leave TODO tests for a later stage.
11. After each stage, run:

```powershell
dotnet test Kiseki.slnx
```

12. If an entity or receipt changes, generate the migration with the repository tool and update the SQLite additive upgrader as applicable:

```powershell
dotnet tool run dotnet-ef migrations add <MigrationName> --project Kiseki.Core --startup-project Kiseki.Core
```

13. Do not edit generated migration designer/snapshot files by hand unless the EF tool cannot run and the user explicitly approves that fallback.

## Final acceptance checklist

- Import with enrichment disabled is behaviorally unchanged.
- Preview remains read-only.
- High-confidence choices are explainable and unique; special volumes never auto-select.
- A series parent with children cannot be linked to one volume.
- Confirmation re-fetches Jiten and never trusts preview/browser metadata.
- Existing link or cover is preserved, including a concurrent change after preview.
- Jiten failure imports reading history unlinked and reports the metadata skip.
- Manual override and TTSU totals retain precedence over Jiten.
- Refresh-review and stale-fingerprint protections still apply to metadata choices.
- Replayed confirmation is idempotent and reports the original metadata outcome.
- Broken images still use the existing browser fallback.
- All tests pass without live external calls.
