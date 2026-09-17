# Gemini follow-up prompt: Stage 1

Paste everything below into Gemini Flash 2.8 High.

---

Continue implementing automatic TTSU metadata enrichment in the current Kiseki working tree.

The previous run reported Stage 0 complete. Its changes are still uncommitted and belong to the user. Preserve them. Do not reset, revert, or recreate them.

Your assignment has two ordered parts:

1. Correct the specific Stage 0 Console edge case described below and verify the correction.
2. Implement **Stage 1 — Add the pure parser and scorer** from `docs/automatic-import-implementation-plan.md`.

Do not implement Stage 2 or later.

## Read first

Read these files completely before editing:

1. `AGENTS.md`
2. `docs/automatic-import-implementation-plan.md`
3. `AUTOMATIC_IMPORT_DESIGN.md`
4. `Kiseki.Console/Screens/JitenLinkScreen.cs`
5. `Kiseki.Tests/JitenLinkScreenTests.cs`
6. `Kiseki.Core/Models/JitenMediaSelection.cs`
7. `Kiseki.Core/Models/JitenSelectionResult.cs`
8. `Kiseki.Core/Services/IJitenSelectionResolver.cs`
9. `Kiseki.Core/Services/JitenSelectionResolver.cs`
10. The existing Jiten and TTSU title-normalization tests and services relevant to this stage.

Treat `docs/automatic-import-implementation-plan.md` as the repository-grounded contract. `AUTOMATIC_IMPORT_DESIGN.md` is product direction only.

Inspect `git status --short` and the complete current diff before changing anything.

## Mandatory Stage 0 correction

The current Console implementation has a stale-search-result edge case:

- `SelectBookAsync` correctly calls the fresh resolver when a search result reports `ChildrenDeckCount == 0`.
- If the resolver returns `ParentHasChildren`, it calls `SelectSubdeckAsync(selectedDeck)`.
- `SelectSubdeckAsync` calls `GetPromptChoices(selectedDeck)`, which still sees the stale `ChildrenDeckCount == 0` and can display **“Link the entire deck”** even though fresh Jiten detail proved that the deck has children.

Fix this so that a subdeck-only path can never offer or display “Link the entire deck,” regardless of the stale search DTO. Prefer making the method contract express that it is a subdeck-only flow instead of mutating the DTO or adding a fragile flag.

Update `JitenLinkScreenTests` with a regression test that exercises the same Console decision path or the exact shared choice-producing method used by that path. The existing test that invokes `JitenSelectionResolver` directly is insufficient evidence of Console behavior. Keep the authoritative resolver check before returning a selection.

Run the relevant Stage 0 tests after this correction. Do not otherwise redesign Stage 0.

## Stage 1 objective

Add a deterministic, explainable, pure Core title parser and Jiten candidate scorer. This stage must make no HTTP calls, perform no EF operations, change no Razor UI, and change no database schema.

Suggested locations from the plan:

```text
Kiseki.Core/Services/Metadata/MediaTitleParser.cs
Kiseki.Core/Services/Metadata/JitenCandidateScorer.cs
Kiseki.Core/Models/Metadata/ParsedMediaTitle.cs
Kiseki.Core/Models/Metadata/JitenMatchCandidate.cs
Kiseki.Core/Models/Metadata/JitenMatchResult.cs
Kiseki.Tests/MediaTitleParserTests.cs
Kiseki.Tests/JitenCandidateScorerTests.cs
```

You may adjust names when the current repository strongly supports a clearer shape, but keep the same responsibilities and explain any deviation.

## Parser contract

The parser must be pure, invariant, conservative, and table-tested.

Return a typed result containing at least:

- the original title;
- an NFKC-normalized comparison title;
- a normalized base/search title;
- an optional structured volume marker;
- whether the marker is special or fractional;
- short parsing notes/evidence suitable for diagnostics.

Use `NormalizationForm.FormKC` and culture-invariant comparisons. Collapse insignificant whitespace consistently.

Support only the tested grammar required by Stage 1:

- file suffixes `.epub`, `.html`, and `.txt`;
- recognized leading bracketed release/publisher tags;
- Arabic terminal volume numbers, including zero-padded forms such as `01`;
- decimal/fractional forms such as `4.5`, marked special;
- explicit `vol`, `volume`, `巻`, and `第…巻` forms;
- Roman numerals only in an explicit volume context such as `Vol. IV`; a bare terminal `I` must not be assumed to be a volume;
- terminal `上`, `中`, and `下` only when unambiguously used as volume markers;
- explicit `Ep.`, `EX`, short-story, and school-year/arc markers, always marked special.

Be deliberately conservative:

- Do not remove arbitrary bracketed subtitles.
- Do not remove internal numbers.
- Do not interpret a title made entirely of digits, such as `86`, as a volume marker when removal would leave no meaningful base title.
- Do not interpret a lone Roman letter as a volume without an explicit volume prefix.
- Do not silently guess unsupported Japanese numeral forms. Return no marker and keep the title intact unless a tested rule recognizes them.

Represent volume identity structurally. Do not pass raw marker strings into scoring. Equivalent supported forms such as `01`, `Vol. 1`, and `第1巻` should compare as the same ordinary volume number.

## Scorer contract

The scorer must accept parsed imported-title input, pure Jiten candidate data, and an optional authoritative TTSU total. It must rank all candidates together so it can calculate the runner-up margin.

It must not call `IJitenApiClient`, `IJitenSelectionResolver`, EF Core, ASP.NET, or the filesystem.

Use the Stage 1 rules from the implementation plan:

### Base title: 0–40 points

- Score 40 for an exact normalized base-title match against original, English, or romaji.
- A conservative partial/token match may score less, but document the exact deterministic rule in code and tests.
- A partial title match must not be able to produce High confidence.
- Record which Jiten title variant matched.

### Volume identity: 0–35 points

- Score 35 for an exact structured volume match.
- Explicitly conflicting volume markers disqualify the candidate.
- Both sides having no volume may score 35 only for a verified standalone candidate with no child decks.
- One side having an explicit marker while the other has none scores 0 for volume evidence.

### Character-count sanity: 0–20 points

- Accept only the optional authoritative inferred TTSU total supplied by the caller.
- Never derive or accept daily characters read as the total.
- Use a symmetric relative difference: `abs(ttsu - jiten) / max(ttsu, jiten)`.
- Difference at most 15% scores 20.
- Difference above 15% and at most 30% scores 10.
- Difference above 30%, a missing total, or non-positive comparison values scores 0.

### Confidence

- High: score at least 85, no hard disqualifier, and either no runner-up or at least a 10-point lead over it.
- Review: score 60–84, a High-scoring candidate with a lead below 10, or any otherwise useful incomplete match.
- None: below 60 or disqualified/no safe candidate.
- A fractional, `EX`, episode, side-story, short-story, arc, or other special marker can never be High; cap it at Review.
- Covers do not contribute to the score.
- Do not add chronology scoring.

Every scored candidate must expose concise evidence, for example:

- `Exact original base title`
- `Volume 3 matched`
- `TTSU total differs by 6.2%`
- `Explicit volume conflict: 2 vs 3`
- `Special volume requires review`

Sort deterministically by:

1. score descending;
2. display title using ordinal comparison;
3. parent deck ID;
4. subdeck ID, with a consistent null ordering.

Keep thresholds and weights centralized constants.

## Required tests

Use table-driven xUnit tests where practical. At minimum cover:

### Parser

- NFKC full-width normalization and whitespace collapse.
- Leading recognized release tag and file suffix removal.
- Arabic `1` and `01` equivalence.
- Explicit `Vol. IV` extraction.
- A bare terminal Roman `I` remaining part of the title.
- `第1巻`, `1巻`, and supported Japanese position markers.
- `4.5`, `EX`, `Ep.1`, side-story/short-story, and arc/school-year markers flagged special.
- Internal numbers retained.
- `86` not reduced to an empty base title or treated as volume 86.
- Unsupported/ambiguous forms preserved without a guessed marker.
- Results independent of the current culture.

### Scorer

- Exact match across each title variant.
- Exact and conflicting volume markers.
- Verified standalone candidate with no volume.
- Missing marker on one side.
- Character-count boundaries at 15% and 30%, plus missing/non-positive totals.
- No use of cover data.
- High threshold, Review threshold, None threshold, and the 10-point runner-up rule.
- Special/fractional cap at Review.
- Deterministic ordering when scores tie.
- Evidence describes the actual contributing and disqualifying rules.

Do not write tests that merely mirror private implementation details. Tests should establish the public parser/scorer contract.

## Existing invariants to preserve

- Do not modify TTSU import, preview, fingerprint, commit, or receipt behavior.
- Do not integrate the parser/scorer into Web or candidate discovery yet.
- Do not make Jiten network calls.
- Do not change `MediaWork`, EF entities, migrations, or `SqliteSchemaUpgrade`.
- Do not add retailer cover logic.
- Keep Core free of ASP.NET/UI dependencies.
- Preserve all unrelated working-tree changes.

## Verification

Run focused tests while iterating, then run all of:

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
git diff --check
git status --short
```

Inspect the final diff and confirm it contains only the Stage 0 correction, Stage 1 implementation/tests, and pre-existing user files.

## Completion report

Report:

- the Stage 0 edge-case correction and its regression test;
- the parser grammar actually supported;
- the exact scoring and confidence behavior;
- files added or changed;
- tests added or updated;
- the exact build/test results, including skipped tests;
- any intentionally unsupported ambiguous title forms for later calibration.

Do not claim Stage 1 complete if the full test suite fails. Stop after Stage 1 and wait for review.

