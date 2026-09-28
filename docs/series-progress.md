# Series Feature — Progress Tracker

> **Notice for New Sessions / Agents**:
> Read this file first before asking the user for context. It maintains the live status of the Series functionality implementation as outlined in [`docs/series-implementation-plan.md`](./series-implementation-plan.md).
> Always update this file whenever a stage or milestone is completed.

---

## Current Status
- **Current Stage**: Stage 4 completed (All 4 Roadmap Stages Implemented & Tested)
- **Status**: Full test suite passing (707/707 passed, 4 skipped PostgreSQL integration tests).
- **Last Updated**: 2026-09-28

---

## Architectural Decisions Agreed Upon
1. **Entity Name**: `SeriesInstallment` represents an installment/volume slot in a series.
2. **Auto-Matching**: When a series is populated from Jiten, automatically match and link any existing `MediaWork` whose `JitenSubdeckId` matches the installment's `JitenSubdeckId`. No complex heuristics needed.
3. **Empty / Manual Series**: Allow creating empty manual series without Jiten (e.g. for series like *Aobuta* where each volume is an independent deck on Jiten rather than subdecks under a main deck).
4. **Scope**: Focus solely on **Books / Light Novels** for now. Defer Franchises and other media types.
5. **Simplicity First**: Solve only essential problems; avoid over-engineering or speculative edge-case handling.
6. **Cover Precedence**: If an installment is matched to a `MediaWork` that has its own cover (e.g. from Google Books, OpenLibrary, TTSU, or user override), display the `MediaWork` cover; otherwise fallback to the installment's Jiten cover.
7. **Design System & Styling**: Use Kiseki dark-mode CSS variables (`var(--color-surface)`, `var(--color-raised)`, `var(--color-edge)`), `.media-grid`, `.media-card`, and `_ProgressBar.cshtml` for visual consistency.

---

## Stage Checklist

- [x] **Stage 1: Domain & Persistence Foundation**
  - [x] Define `SeriesInstallment` entity in `Kiseki.Core/Entities/`
  - [x] Update `MediaSeries` with `Installments` collection, `CoverUrl`, and progress aggregations
  - [x] Register `SeriesInstallments` in `ImmersionDbContext`
  - [x] Generate EF Core migration (`AddSeriesInstallments`)
  - [x] Add unit tests for calculation logic (`Kiseki.Tests/SeriesInstallmentTests.cs`)
  - [x] Verify test suite passes cleanly (679 passing tests)
  - [x] **User Review & Approval**

- [x] **Stage 2: Series Creation (`/Series/Create`)**
  - [x] Implement Jiten subdeck search & import mode in `SeriesService`
  - [x] Implement frictionless auto-matching of existing `MediaWork`s
  - [x] Implement manual / empty series creation mode
  - [x] Implement `/Series/Create` Razor Page & PageModel
  - [x] Add unit & integration tests (`SeriesServiceTests` and `SeriesCreatePageTests`)
  - [x] **User Review & Approval**

- [x] **Stage 3: Series Details Page & Installment Management (`/Series/Details/{id}`)**
  - [x] Render aggregate progress bar and volume counts
  - [x] Render installment list with covers and progress
  - [x] Implement manual work linking / unlinking
  - [x] Implement manual installment addition (for standalone deck series)
  - [x] Add tests for details and installment actions (`SeriesServiceTests` and `SeriesDetailsPageTests`)
  - [x] Refine dark theme styling to match Kiseki aesthetic
  - [x] **User Review & Approval**

- [x] **Stage 4: Series Index (`/Series/Index`)**
  - [x] Implement `GetAllSeriesAsync` in `ISeriesService` and `SeriesService`
  - [x] Create `SeriesListItemViewModel` with aggregate metrics, cover fallback, and progress labels
  - [x] Implement `/Series/Index` Razor Page & PageModel with search query and media type filtering
  - [x] Render series cards with `.media-grid`, `.media-card`, progress bar, volume counts, and empty state
  - [x] Add unit and page model tests (`SeriesServiceTests` and `SeriesIndexPageTests`)
  - [x] Verify full test suite passes cleanly (707 passed)
  - [ ] **User Review & Approval**

---

## Next Action
User review and testing of **Stage 4** (`/Series/Index`). Start the local server (`dotnet run --project Kiseki.Web`), verify the Series gallery/cards, filtering, and navigation.
