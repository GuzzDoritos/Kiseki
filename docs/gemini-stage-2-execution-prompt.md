# Gemini execution prompt: implement Stage 2 now

Paste everything below into Gemini Flash 2.8 High in response to its proposed Stage 2 plan.

---

Your implementation plan is approved with the amendments below. **Implement it now.** Do not return another plan, proposal, checklist for approval, or “user review required” section. Make the code and test changes in the current working tree, run verification, and return the completion report requested in `docs/gemini-stage-2-prompt.md`.

Continue to preserve all existing uncommitted Stage 0/1 changes and unrelated user files. Do not reset or revert anything.

## Required amendments to your proposal

1. **No Stage 3 registration or integration**
   - Do not modify `Kiseki.Web/Program.cs` to register the match service.
   - Do not modify `Kiseki.Console/Program.cs`.
   - The Console application does not use a DI container, and preview integration belongs to Stage 3.
   - Construct Stage 2 services directly in tests.

2. **Use the future batch book key type**
   - Use `Guid CorrelationId`, not `string`, in `JitenMatchRequest` and `JitenMatchOutcome`.
   - Reject `Guid.Empty` and duplicate correlation IDs before any HTTP call.

3. **Keep the caller-supplied time budget explicit**
   - Use a required `TimeSpan timeBudget` parameter on `MatchBatchAsync`; do not make it nullable and do not hide it behind `DefaultTimeBudget`.
   - Reject non-positive budgets before any HTTP call.
   - `JitenMatchOptions` should contain only operational retry/concurrency defaults actually owned by the service.

4. **Do not hide `HttpRequestException.StatusCode`**
   - `JitenHttpException` should derive from `HttpRequestException`, initialize the inherited status code through the base constructor, and add only the missing data such as `RetryAfter`.
   - Preserve existing `catch (HttpRequestException)` behavior in Web and Console.

5. **Use one shared hierarchy implementation**
   - Add the proposed pure resolution overload for already-fetched `JitenDeckDetailDTO` and make `ResolveAsync` delegate to it.
   - Candidate discovery must use that overload with its cached detail; it must not re-fetch once per subdeck.
   - Fresh detail, not search-result `ChildrenDeckCount`, decides standalone versus parent-with-children.

6. **Preserve safe cover evidence**
   - Build match candidates from verified `JitenMediaSelection` values, not raw `CoverName` strings.
   - Retain normalized HTTPS URLs and `JitenCoverEvidence`.
   - Cover data must remain score-neutral.

7. **Observe all started work**
   - When the internal budget expires, cancel via the linked token, await/observe all started tasks, and return `Unavailable` for unfinished requests.
   - Caller cancellation must still propagate `OperationCanceledException`.
   - Do not leave background tasks or unobserved exceptions.

## Required tests omitted from your proposed test list

In addition to the tests you listed, implement all of these from the approved Stage 2 prompt:

- empty batch makes no HTTP calls;
- `Guid.Empty`, duplicate correlation IDs, and non-positive budget fail before HTTP;
- empty/unusable parsed query returns `NoCandidates` without HTTP;
- search results with `ParentDeckId` load and verify the parent detail;
- invalid/non-positive Jiten IDs are skipped with warnings;
- duplicate candidates are removed by `(DeckId, SubdeckId)`;
- safe cover URL and `JitenCoverEvidence` survive conversion;
- the same discovered candidates are scored separately for different input volumes and TTSU totals;
- ordinary non-retryable 4xx is not retried;
- malformed JSON/schema failure is not retried;
- caller cancellation is not retried and propagates;
- one failed detail branch does not erase a candidate from another successful branch;
- when usable candidates remain after a branch failure, return `Matched` with warnings;
- exhausted 429 returns `RateLimited`; exhausted 5xx returns `Unavailable`;
- output order exactly matches input order;
- the 100-book synthetic batch never exceeds configured concurrency and uses no real delays or network.

Also implement the Stage 1 regression tests already proposed:

- oversized integer and decimal markers never throw and remain unparsed;
- invariant evidence under a non-English culture;
- consistent cross-language volumes work;
- conflicting cross-language volumes disqualify and cannot become High.

## Scope reminder

Do not modify TTSU PageModels, Razor pages, preview/batch models, JavaScript, CSS, entities, receipts, migrations, or SQLite upgrade logic. Do not add global caching, background jobs, retailer sources, or live-network tests. Stop after Stage 2.

## Execute and verify

Make the changes now. Run focused tests while working, then run exactly:

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
git diff --check
git status --short
```

Inspect the final diff for accidental Stage 3 work. Then provide a factual completion report with changed files, implemented behavior, test counts/results, skipped tests, and any remaining limitations. Do not claim completion if the full suite fails.

