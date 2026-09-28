# Series Reading Time Estimation — Progress Tracker

> **Notice for Future Sessions / Agents**:
> Read this file first before asking the user for context. It maintains the live status of the Series Reading Time Estimation feature outlined in [`docs/series-time-estimate-plan.md`](./series-time-estimate-plan.md).
> Always update this file whenever a stage or milestone is completed.

---

## Current Status
- **Current Stage**: Stage 3 completed (Feature Implementation Complete)
- **Status**: Full test suite passing (731/731 passed, 4 skipped). Overview header and installment cards updated with reading time and calendar estimates. Ready for user testing.
- **Last Updated**: 2026-09-28

---

## Decisions Agreed Upon
1. **Dual Time Representation**:
   - Both **Calendar Time** (e.g., `~1 mo 12 d`) and **Immersion Reading Time** (e.g., `38h 20m`) are shown.
2. **Speed & Pace Metric**:
   - **Rolling 30-day average** (characters/hour and characters/calendar day).
   - Fallback to **all-time average** if recent 30-day immersion activity is < 60 minutes.
   - Non-intrusive fallback messaging if no valid timed logs exist.
3. **Dual Scope**:
   - **Series-level**: In the Series Details Overview header stats grid.
   - **Volume-level**: In each installment item card/row in the volume list.
4. **No Automated Browser Testing**:
   - As per AGENTS.md, all testing is validated via unit and integration tests (`dotnet test`). User performs interactive browser testing.

---

## Stage Checklist

- [x] **Stage 1: Core Pace & Estimation Engine (`Kiseki.Core` + Tests)**
  - [x] Define `ReadingPaceProfile` and `ReadingTimeEstimate` models in `Kiseki.Core`
  - [x] Implement `IReadingPaceService` / `ReadingPaceService` with 30-day rolling window & all-time fallback
  - [x] Implement duration and calendar formatters (handling minutes, hours, days, months)
  - [x] Add unit tests in `Kiseki.Tests` for speed, pace, fallbacks, estimates, and formatting
  - [x] **User Review & Approval**

- [x] **Stage 2: Service & ViewModel Integration (`Kiseki.Core` & `Kiseki.Web`)**
  - [x] Register `IReadingPaceService` in DI
  - [x] Augment `SeriesDetailsViewModel` and `SeriesInstallmentViewModel` with estimate properties
  - [x] Update `Series/Details.cshtml.cs` to calculate and attach estimates
  - [x] Add tests for PageModel and ViewModel projection
  - [x] **User Review & Approval**

- [x] **Stage 3: UI Presentation in Series Details (`Kiseki.Web`)**
  - [x] Add "Est. time left" tile with calendar time + reading time in `.detail-stat-grid`
  - [x] Add volume-level remaining time badge to installment items
  - [x] Handle empty/fallback states (uncounted volumes, no logs, completed series)
  - [x] Refine CSS styling to match dark-mode design system
  - [x] Verify with test suite (731 tests passing)
  - [x] **Ready for User Testing**
