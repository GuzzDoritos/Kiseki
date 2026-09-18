# Kiseki: Stage 6A Handoff & Breadcrumbs

## Current Repository State

- **Branch**: `main` is clean and up to date with `origin/main`.
- **Completed & Pushed**: Stages 0, 1, 2, 3, 4, and 5 are fully implemented, verified, committed, and pushed.
- **Active Task**: Continue implementing automatic TTSU metadata enrichment by executing **Stage 6A**.

---

## Instructions for Next Session

1. **Read and Follow Prompt**:
   - The primary instruction file for this step is [`docs/antigravity-stage-6a-prompt.md`](./antigravity-stage-6a-prompt.md).
   - Also consult [`docs/automatic-import-implementation-plan.md`](./automatic-import-implementation-plan.md), specifically the Stage 6 section and acceptance checklist.

2. **Stage 6A Objective**:
   - **Provider-neutral cover persistence and provenance**: Replace the Jiten-specific `JitenCoverUrl` on `MediaWork` with generic `CoverUrl` and `CoverSource` (`MediaCoverSource`).
   - Distinguish `None = 0`, `LegacyUnknown = 1`, `Jiten = 2`, `JitenParentFallback = 3`, `UserOverride = 4`.
   - Protect user overrides and migrated legacy covers from being overwritten by Jiten linking, relinking, unlinking, or automatic TTSU enrichment.
   - Generate EF Core PostgreSQL migration `AddMediaWorkCoverProvenance` and update `SqliteSchemaUpgrade`.
   - Update Razor pages (`Details`, `LinkJiten`, `_MediaWorkRow`, `_MediaWorkCard`) and JS components.

3. **Strict Scope Boundaries (What NOT to do)**:
   - Implement **Stage 6A only**. Do **NOT** implement Stage 6B.
   - Do **NOT** add Amazon Japan, BookWalker, or retailer scraping.
   - Do **NOT** add external image downloaders, MIME/dimension inspection, or local image caching.
   - The Stage 5 no-preference auto-match default remains **off** (`false`). Do not enable it in Stage 6A.

---

## Verified Baseline Before Starting Stage 6A

```text
dotnet build Kiseki.slnx:
    Succeeded: 0 warnings, 0 errors

dotnet test Kiseki.slnx:
    Passed:  350
    Skipped: 4 (opt-in PostgreSQL integration tests requiring KISEKI_TEST_POSTGRES)
    Failed:  0
    Total:   354

Focused calibration + TTSU page slice:
    93 passed, 0 failed

Calibration corpus:
    31 cases; 17 High, 11 Review, 3 None; zero false High

dotnet-ef migrations has-pending-model-changes:
    No changes have been made to the model since the last migration.

git diff --check:
    Clean (0 whitespace or formatting errors)

git status:
    Working tree clean, up to date with origin/main
```

---

## Recent Commits

```text
98408da docs: clean up legacy stage prompts and add Stage 6A prompt
88da99d test(core): add Jiten match calibration corpus, test harness, and manual testing review
7f38b92 feat(core): apply verified metadata in import transaction with receipt counters and migration
538329d feat(web): add batch TTSU import metadata preview, candidate review, and UI badges
ecd5bed feat(core): implement Jiten match service, candidate scorer, and selection resolver
```

