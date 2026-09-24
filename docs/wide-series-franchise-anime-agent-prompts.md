# Finish whole-series book progress: shortened agent prompts

Updated 2026-09-24. This replaces the previous 18-prompt runbook for the current checkout. **Start with Prompt A, not the old Batch 0 or anime research prompt.** The user's priority is progress across an entire book series, including volumes that have never been imported.

The [architecture proposal](wide-series-franchise-anime-plan.md) and [implementation plan](wide-series-franchise-anime-implementation-plan.md) remain background documents. Their remaining expansion batches are deferred. The [progress log](wide-series-franchise-anime-progress.md) records historical work; its old instruction to proceed to Prompt 13 is superseded by this runbook. Do not restart completed batches.

## Current progress and actual remaining work

Inspection of the working tree, progress log, services, Razor views, and tests shows:

| Area | Current state | What to do now |
| --- | --- | --- |
| Canonical installments and tracked copies, Batches 1A/1B | Implemented, including migrations and SQLite upgrades | Preserve; no redesign or destructive cleanup |
| Manual Series pages and progress, Batches 2A/2B | Implemented | Make the headline useful and correct for the whole series |
| Jiten catalogue population, Batches 3A/3B | Implemented with reviewed refresh | Reuse it to discover unimported volumes |
| TTSU installment/copy linking, Batches 4A/4B | Implemented and previously reviewed | Keep and regression-check |
| Franchise pages and graph sync, Batches 5A/5B | Already implemented | Leave intact; no further franchise expansion |
| Anime, Batches 6/7 | Not implemented according to the progress log | Deferred; no provider research report required |
| PostgreSQL upgrade/concurrency verification | Six environment-gated tests remain skipped | Separate release prerequisite, not a reason to build more features |

The current calculator already treats catalogue-only released volumes as zero progress and counts each installment once. The main work is finishing presentation and a few correctness edges, not building another system.

Verification for this scope review on 2026-09-24: `dotnet test Kiseki.slnx --no-restore` passed with **762 passed, 6 skipped, 0 failed**. The skipped tests are PostgreSQL integration tests. No new browser acceptance was run during this documentation update; prior browser evidence remains in the progress log.

Concrete findings from this inspection:

- `SeriesProgressCalculator` computes character-weighted progress only for released/included volumes with usable totals and progress. Missing data can shrink that denominator; the result must not be labelled as an unqualified whole-series percentage.
- Series Details leads with "Weighted Progress" and seven technical coverage panels. It lacks a prominent completed-volumes/total-volumes summary. Index can mark a series completed from the weighted percentage alone, even when that percentage describes only the known subset.
- Jiten entries without release dates become `Unknown`; they stay outside the released denominator. Keep that uncertainty visible and provide an obvious path to the existing release-state correction controls. Do not silently assume every provider entry is released.
- `SeriesCatalogueQueryService.GetDetailsAsync` calculates its summary from the displayed installment slice (maximum 500), while Index reads all installments for its summaries. Whole-series totals must not change with the number of rows displayed.
- There is substantial uncommitted and untracked implementation work. Preserve it and unrelated changes. The historical progress report is evidence of previous checks, not a substitute for checking the current diff.

## What counts as finished

A user can create a book series, populate its known volumes from Jiten or manually, and see their progress without importing every volume first.

The normal Series view should lead with:

- **12 / 46 released volumes completed**, regardless of character metadata availability.
- **14 tracked; 32 not tracked**, using the same released/included scope.
- A secondary **reading progress by character count** percentage, with a short coverage note when incomplete. Partial reading still matters; volume completion and character progress answer different questions.
- The ordered volume list, including unimported volumes, with simple tracking/completion states.

The denominator is the known catalogue of included, confirmed released volumes, not just imported books and not a promise that Jiten knows every published volume. Show upcoming, excluded, and release-unknown entries separately. Missing character totals must not remove a released volume from the completion count.

Reference acceptance case: four released/included volumes, each 100,000 characters; Volume 1 has a completed copy, Volume 2 is halfway read, and Volumes 3/4 have no copies. Expected: **1/4 completed, 2/4 tracked, 37.5% character progress**. A second copy of Volume 1 must not change the denominator. Importing Volume 3 must use the same installment. If Volume 4's total becomes unknown, completion remains 1/4 and character progress is explicitly partial, with coverage shown.

Keep the existing data model, reviewed provider refresh, copy history, TTSU receipts, manual corrections, and Library boundary. Catalogue-only volumes must not appear as tracked books in Library. No anime, new provider, generalized framework, schema consolidation, or additional franchise work is needed to finish this scope.

## How to run the remaining work

Use A, then B, then C, sequentially in the same updated checkout. Stop any previous agent before switching. Select the indicated model/reasoning in the interface before pasting. These retain the earlier task-based model recommendations; no new model comparison is needed.

The shared rules below apply to each prompt. Each prompt references this file so a fresh chat can read them.

- Inspect existing changes and finish missing work; do not reset or reimplement working features.
- Make the smallest change satisfying the acceptance case. Business rules stay in Core. Reuse existing commands, queries, UI conventions, and provider review infrastructure.
- Run meaningful tests for changed behavior and `dotnet test Kiseki.slnx` once the batch is ready. Repeat after fixes, not just to fill time. No live provider calls in automated tests.
- Monitor one test process at a time using its actual session/status tool. No repeated "still waiting" messages. Use waits of at most 60 seconds; investigate after 2 minutes with no output. At 5 minutes diagnose a stalled run, stop only your own stalled process, and retry only for an identified fix (at most once for an unexplained hang). Report unavailable verification honestly.
- Update the existing progress document with actual changes/results and the next prompt. Do not create another architecture plan or handoff framework. Do not deploy or modify production data.

## Prompt A — Codex: finish whole-series progress contracts

**Model: GPT Sol | Reasoning: high.** This touches aggregation and compatibility; it is not a fresh architecture task.

```text
Finish the Core/query portion of the shortened scope in
 docs/wide-series-franchise-anime-agent-prompts.md (Prompt A).
Read that file, AGENTS.md, decisions, and current progress. Follow the shared
execution rules. Inspect git status and existing changes; preserve them.

The priority is whole-series book progress, including unimported volumes.
Do not restart old batches or implement anime/franchise expansion.

Reuse SeriesProgressCalculator and SeriesCatalogueQueryService. Expose clear
completed, total released/included, tracked, and untracked counts in the
existing immutable result contracts. Count each installment once; any
explicitly completed copy completes it. Missing character totals must not
remove volumes from completion counts. Preserve partial reading and existing
edition/bookmark precedence in the character-weighted metric.

Make whole-series summaries independent of displayed row limits. Keep reads
no-tracking and use scalar/grouped queries rather than loading full histories.
Expose enough coverage to distinguish partial character progress from complete
series completion. Preserve unknown-release/upcoming/exclusion semantics;
no guesses about release status and no new schema unless demonstrably needed.

Add focused regressions for the four-volume acceptance example, multiple
copies, missing totals/unknown progress, no eligible volumes, and a series
larger than the Details row limit. Ensure Index and Details summaries agree.
Preserve existing TTSU/Jiten behavior and franchise callers.

Run verification and record exact contract changes and the small allowed
UI scope for Prompt B in the existing progress log. If a requirement already
works, reuse it. Stop after A; do not redesign the pages in this task.
```

## Prompt B — Gemini: make the Series page simple and useful

**Model: Gemini Flash 3.8 | Reasoning: medium.** UI work against the tested Prompt A contracts.

```text
Implement Prompt B of docs/wide-series-franchise-anime-agent-prompts.md.
Read that file, AGENTS.md, and Prompt A's progress entry/contracts. Follow the
shared execution rules. Preserve existing work and finish only this UI scope.

Simplify Series Index and Details around the whole-series acceptance example:
show completed/released volumes first, tracked/not-tracked counts next, then
character-based reading progress with a concise partial-coverage label when
needed. Never mark the whole series complete just because a known subset has
100% character progress. Handle empty and unavailable metrics honestly.

Keep all volumes visible in order, including catalogue-only volumes. Retain
Create/Edit, add volume, refresh from Jiten, and track/link-copy actions.
Keep existing edit/order/association/review functionality accessible, but put
technical coverage, raw order keys, copy management, and advanced controls
behind compact details/edit sections. Use plain book terminology. Do not
replace the existing framework, refresh flow, or import UI.

Show unknown release states with a clear link/control to the existing manual
correction workflow; provider entries must not silently disappear from the
user's understanding of series scope. Preserve upcoming/excluded labels and
review safeguards. Do not add a new matching algorithm or approval wizard.

Work in Series Razor pages/view models, narrowly scoped CSS/JS, and relevant
PageModel/render tests. Calculations belong in Core; report contract gaps for
Codex instead of recreating formulas in views. Avoid unrelated franchise UI.

Test the four-volume case and incomplete metadata states. Verify desktop and
mobile browser presentation using disposable data and the existing browser
tooling where available. Run the full suite, record evidence/gaps in progress,
and stop. Prompt C owns final integration and acceptance.
```

## Prompt C — Codex: review, demonstrate the workflow, and stop

**Model: GPT Sol | Reasoning: high.** Final integration review; no next feature batch.

```text
Complete Prompt C of docs/wide-series-franchise-anime-agent-prompts.md.
Read that file, AGENTS.md, and current progress; follow the shared execution
rules. Review actual A/B changes and fix only defects in this reduced scope.

Verify the user can create a series, add its full known catalogue manually
or through the existing reviewed Jiten refresh, and see whole-series progress
with only some books imported. Use disposable SQLite and stubbed provider
data. Verify the four-volume acceptance case, incomplete metadata labels,
release-state corrections, multiple copies counted once, and identical
Index/Details totals even when the list is truncated.

Use the existing TTSU flow to import a catalogue-only volume; prove the
installment count stays fixed while tracked count/progress changes. Confirm
untracked volumes stay out of Library, refresh preserves corrections/history,
and existing franchise and Console behavior has not regressed. Reuse current
tests and add regressions only for new defects.

Run dotnet test Kiseki.slnx and the focused persisted browser workflow. Report
PostgreSQL checks as passed or skipped based on actual execution. PostgreSQL
migration execution remains a separate release prerequisite when unavailable;
do not hold local feature completion open for more feature development.

Update progress to make anime/further expansion deferred and remove the stale
instruction to proceed to old Prompt 13. Report remaining release blockers
separately. Give a short user guide with the actual routes/actions for create
series -> populate volumes -> link/import books -> read progress.

Stop when this workflow works. Do not begin another batch, remove existing
franchise code, redesign persistence, deploy, or touch production data.
```

After C, use the feature. New work should come from an observed problem or an explicit request, not the remaining chapters of the original roadmap.
