# Gemini follow-up prompt: Stage 2

Paste everything below into Gemini Flash 2.8 High.

---

Continue implementing automatic TTSU metadata enrichment in the current Kiseki working tree.

Stages 0 and 1 are present but still uncommitted. Their changes belong to the user. Preserve them and all unrelated files. Do not reset, revert, or recreate them.

Your assignment has two ordered parts:

1. Make the three targeted Stage 1 corrections below and add regression tests.
2. Implement **Stage 2 — Add bounded Jiten candidate discovery** from `docs/automatic-import-implementation-plan.md`.

Do not implement Stage 3 or any Web preview UI.

## Read first

Read these files completely before editing:

1. `AGENTS.md`
2. `docs/automatic-import-implementation-plan.md`
3. `AUTOMATIC_IMPORT_DESIGN.md`
4. Every file under `Kiseki.Core/Models/Metadata/`
5. Every file under `Kiseki.Core/Services/Metadata/`
6. `Kiseki.Core/Services/IJitenApiClient.cs`
7. `Kiseki.Core/Services/JitenApiClient.cs`
8. `Kiseki.Core/Services/IJitenSelectionResolver.cs`
9. `Kiseki.Core/Services/JitenSelectionResolver.cs`
10. `Kiseki.Core/Models/JitenMediaSelection.cs`
11. `Kiseki.Core/DTOs/JitenDeckDTO.cs`
12. `Kiseki.Tests/JitenApiClientTests.cs`
13. `Kiseki.Tests/JitenSelectionResolverTests.cs`
14. `Kiseki.Tests/MediaTitleParserTests.cs`
15. `Kiseki.Tests/JitenCandidateScorerTests.cs`

Inspect `git status --short` and the complete current diff before editing. Treat `docs/automatic-import-implementation-plan.md` as the repository-grounded contract and `AUTOMATIC_IMPORT_DESIGN.md` as product direction only.

## Mandatory Stage 1 corrections

Keep these fixes narrow. Do not redesign the parser or change supported grammar beyond the added safety cases.

### 1. Arbitrary external titles must not throw numeric overflow

`MediaTitleParser` currently uses `int.Parse`/`decimal.Parse` after regex matches. A TTSU or Jiten title containing an extremely long terminal number can throw `OverflowException` and abort enrichment.

- Replace unsafe numeric parsing with invariant `TryParse` handling.
- When a recognized-looking marker is outside the supported numeric range, preserve it as part of the base title and return no guessed volume marker.
- Add parser tests for oversized integer and decimal markers.
- Parsing any non-null string must return a result rather than throw because of numeric size.

### 2. Evidence formatting must be culture-invariant

`JitenCandidateScorer` formats percentage evidence with the current culture.

- Format score evidence using `CultureInfo.InvariantCulture` so diagnostics are deterministic.
- Add a scorer test under a non-English culture that asserts stable evidence text.
- Restore the original culture in `finally`.

### 3. Candidate volume evidence must be consistent across title variants

The scorer currently matches a base title against one variant but independently chooses the first volume found in original/English/romaji order. In inconsistent DTOs, that can score or disqualify using a different title variant than the one that matched.

Refactor candidate-title parsing so each non-empty variant is parsed once per candidate and title/volume evidence is evaluated coherently:

- Retain the matched title variant.
- If explicit volumes across non-empty candidate title variants conflict, disqualify the candidate with clear evidence.
- Otherwise use the one consistent candidate volume, preferring the matched variant's marker when available.
- Do not allow one variant saying volume 1 and another saying volume 2 to become High confidence.
- Add tests for consistent cross-language variants and conflicting cross-language variants.

Run the focused Stage 1 tests after these corrections.

## Stage 2 objective

Add a Core service that accepts a batch of imported-title requests, performs bounded and de-duplicated Jiten discovery, expands and verifies hierarchy, invokes the existing pure scorer, and returns one typed outcome per input request.

The service must degrade per book. A failed search or detail request must not throw away successful results for unrelated books.

Suggested files:

```text
Kiseki.Core/Models/Metadata/JitenMatchRequest.cs
Kiseki.Core/Models/Metadata/JitenMatchOutcome.cs
Kiseki.Core/Services/Metadata/IJitenMatchService.cs
Kiseki.Core/Services/Metadata/JitenMatchService.cs
Kiseki.Core/Services/Metadata/JitenMatchOptions.cs
Kiseki.Core/Services/JitenHttpException.cs
Kiseki.Tests/JitenMatchServiceTests.cs
```

Names may be adjusted to fit the existing Stage 1 types, but preserve these responsibilities.

## Batch service contract

Prefer one batch API shaped approximately like:

```csharp
Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
    IReadOnlyList<JitenMatchRequest> requests,
    TimeSpan timeBudget,
    CancellationToken cancellationToken = default);
```

Each request needs:

- a caller-provided correlation ID, such as the future batch book key;
- raw imported title;
- optional authoritative TTSU total.

Each outcome needs:

- the same correlation ID;
- a typed status such as `Matched`, `NoCandidates`, `Unavailable`, or `RateLimited`;
- the pure `JitenMatchResult` when candidates were scored;
- concise warnings/failure information safe for later preview display.

Requirements:

- Preserve input order in the returned outcomes.
- Reject duplicate correlation IDs before making HTTP calls.
- An empty input returns an empty result without HTTP calls.
- An empty/unusable parsed search title returns `NoCandidates` without HTTP calls.
- Keep Core models independent of Web batch types and ASP.NET.
- The caller supplies the authoritative total. Stage 2 must not derive it from daily characters read.

## Search and detail de-duplication

For one `MatchBatchAsync` operation:

1. Parse every input title with `IMediaTitleParser`.
2. Group identical normalized base/search queries with ordinal-ignore-case semantics.
3. Call `SearchBooksAsync` once per distinct query.
4. Cache deck-detail tasks by authoritative parent deck ID so duplicate search rows, child rows, and requests sharing a series do not repeat detail calls.
5. Score the resulting candidate set separately for every original request because its parsed volume and TTSU total can differ.
6. De-duplicate candidates by `(DeckId, SubdeckId)` before scoring.

Do not add a process-wide metadata cache in this stage. All search/detail caching is scoped to one batch operation.

## Hierarchy resolution

Search DTO hierarchy hints are not authoritative. Fresh deck detail is required before a candidate is presented as safe:

- For a search result with `ParentDeckId`, load the parent detail using that ID.
- For a result representing a parent or apparent standalone deck, load detail using its deck ID.
- Use fresh detail to determine whether it is standalone or has subdecks.
- If fresh detail has children/subdecks, add only verified subdeck candidates. Never add its parent as a work candidate.
- If fresh detail proves it is standalone, add the verified parent candidate.
- Ignore invalid/non-positive IDs with an outcome warning rather than manufacturing a candidate.
- De-duplicate siblings discovered through multiple search rows.

Avoid duplicating Stage 0's hierarchy rules. Refactor `JitenSelectionResolver` only as needed so a pure validation method/overload can resolve an already-fetched `JitenDeckDetailDTO`. Its existing async method should fetch detail and delegate to the same validation logic. `JitenMatchService` can then use cached detail with that shared logic instead of triggering another HTTP call for every candidate.

When converting a verified `JitenMediaSelection` to `JitenMatchCandidate`:

- preserve normalized HTTPS cover URL behavior;
- preserve `JitenCoverEvidence` (`Specific`, `ParentFallback`, or `None`);
- do not copy unsafe/raw `CoverName` directly;
- remember that cover presence contributes zero score.

Extend `JitenMatchCandidate` as needed to retain cover evidence for Stage 3.

## Concurrency

- Use one `SemaphoreSlim` shared by the batch operation, not one semaphore per book/query.
- Default maximum concurrent Jiten client operations to 3 through a plain Core options object.
- Every search and detail call must pass through the same limiter.
- Hold a permit only while awaiting the Jiten client call. Release it before retry delay/backoff.
- Always release in `finally`.
- Validate the configured concurrency is positive.

`SearchBooksAsync` and `GetDeckDetailAsync` paginate internally; treating each complete client method call as one limited operation is acceptable.

## HTTP error and retry contract

`JitenApiClient` currently loses `Retry-After` when a failed response becomes a generic `HttpRequestException`. Add a small typed exception that remains compatible with existing catch sites, preferably by deriving from `HttpRequestException`, and carries:

- HTTP status code;
- optional parsed `Retry-After` delay.

Refactor `JitenApiClient` response handling so:

- existing 404-to-null behavior for detail/franchise is preserved;
- successful JSON and pagination behavior is preserved;
- non-success responses throw the typed exception while response headers are available;
- cancellation tokens are still passed to send/read operations;
- responses are disposed;
- existing manual Web/Console `HttpRequestException` handling continues to work.

Retry only:

- HTTP 429;
- transient HTTP 5xx.

Rules:

- At most two retries after the initial attempt.
- Respect `Retry-After` when supplied.
- Otherwise use bounded exponential delay plus small jitter.
- Do not retry ordinary 4xx, JSON/schema errors, invalid data, or cancellation.
- Make retry delay testable without real waits, for example through zero-delay test options or a small injected delay abstraction. Do not add a large resilience package for this.
- After retry exhaustion, return `RateLimited` for 429 and `Unavailable` for transient/server failures.

Add `JitenApiClientTests` for typed status and `Retry-After` propagation while retaining pagination coverage.

## Time budget and cancellation

- Apply one overall caller-supplied time budget to the batch.
- A non-positive budget is invalid and must fail before HTTP calls.
- When the internal budget expires, mark unfinished outcomes `Unavailable` and await/observe all started tasks so there are no background or unobserved failures.
- When the caller's cancellation token is cancelled, propagate `OperationCanceledException`; do not convert it to an availability outcome.
- Pass the linked token through semaphore waits, retry delays, and all client calls.

## Partial-failure behavior

- A failed search affects only requests sharing that query.
- A failed detail call must not erase candidates obtained from other successful detail calls.
- If usable candidates remain, score them and return `Matched` with warnings describing skipped discovery branches.
- If no usable candidates remain, return `RateLimited` when rate limiting was the decisive failure, otherwise `Unavailable` or `NoCandidates` as appropriate.
- Malformed Jiten JSON is unavailable data, not retryable data.
- Do not throw recoverable Jiten availability failures through the batch API.

## Required tests

Use stub `IJitenApiClient` implementations and `StubHttpMessageHandler`; never call live Jiten.

At minimum test:

- empty batch and duplicate correlation IDs;
- empty/unusable search title;
- one search per distinct normalized base title;
- two volumes sharing one base query reuse search and parent detail;
- duplicate parent/child search rows reuse detail and candidates;
- fresh standalone verification;
- expansion of parents into subdecks only;
- `ParentDeckId` verification;
- parent candidate exclusion when fresh detail reveals children;
- candidate de-duplication by parent/subdeck IDs;
- preservation of safe cover URL and `JitenCoverEvidence`;
- invalid IDs are skipped with warnings;
- separate scoring per request title/total;
- maximum observed concurrency never exceeds the configured limit;
- 429 retry, `Retry-After`, retry exhaustion, and `RateLimited` result;
- 5xx retry and exhausted `Unavailable` result;
- no retry for ordinary 4xx, malformed JSON, or cancellation;
- one failed detail branch alongside another successful candidate branch;
- internal budget expiry returns unavailable outcomes without leaked work;
- caller cancellation propagates;
- output order matches input order.

The synthetic 100-book concurrency test must complete without exceeding the configured limit and without real external requests.

## Out of scope

- Do not modify the TTSU Razor Page, batch store, preview view models, JavaScript, or CSS.
- Do not register the match service in Web DI yet unless compilation genuinely requires it; Stage 3 owns preview integration.
- Do not apply metadata to `MediaWork`.
- Do not change entities, receipts, migrations, or `SqliteSchemaUpgrade`.
- Do not add background jobs or a global cache.
- Do not implement retailer cover sourcing.
- Do not weaken Stage 0 parent-deck protection or Stage 1 score thresholds.

## Verification

Run focused tests while iterating, then run:

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
git diff --check
git status --short
```

Inspect the final diff for accidental Stage 3 work or unrelated changes.

## Completion report

Report:

- the three Stage 1 corrections and their regression tests;
- the final batch service API and statuses;
- hierarchy expansion and de-duplication behavior;
- concurrency, retry, time-budget, and cancellation behavior;
- files added or changed;
- exact build/test results, including skipped tests;
- any cases deliberately returned as unavailable/reviewable rather than guessed.

Do not claim Stage 2 complete if the full suite fails. Stop after Stage 2 and wait for review.

