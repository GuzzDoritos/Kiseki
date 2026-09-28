# Series Functionality — Implementation Plan

## Overview & Philosophy

The Series feature groups installments of a medium (starting with **Books / Light Novels**) into a single container so the user can track progress across an entire series.

### Core Principles
- **Keep it Simple**: Only solve the immediate, essential problems. Avoid speculative edge cases and excessive automation.
- **Stage-by-Stage Delivery**: Each stage is small, self-contained, and accompanied by automated tests.
- **Review & Test Gate**: We complete one stage, you review and test it, and only then do we move forward.
- **Mandatory Progress Tracking (`docs/series-progress.md`)**:
  - The agent MUST keep `docs/series-progress.md` updated after each stage or significant milestone.
  - This file records: current stage, completed items, next immediate actions, design decisions, and testing results.
  - Any new chat session must consult `docs/series-progress.md` first to resume without requiring the user to re-explain context.

---

## Domain Model

### 1. `SeriesInstallment` (New Entity)
Represents a single slot or volume in a series, regardless of whether it is already in the user's library.

- `Guid Id`
- `Guid MediaSeriesId` (Parent series)
- `int SequenceNumber` (1, 2, 3... for ordering)
- `string Title` (e.g. "Volume 1", or Jiten subdeck title)
- `int? JitenSubdeckId` (Optional link to Jiten deck/subdeck ID)
- `int JitenCharacterCount` (Estimated character count from Jiten, defaults to 0)
- `string? CoverUrl` (Cover image for this volume)
- `Guid? MediaWorkId` (Optional foreign key to a `MediaWork` in the user's library)
- `MediaWork? MediaWork` (Navigation property)

#### Character & Progress Rules for an Installment:
- **Total Characters**:
  If linked to a `MediaWork` and `MediaWork.TotalCharacters > 0`, use `MediaWork.TotalCharacters`.
  Otherwise, fallback to `JitenCharacterCount`.
- **Characters Read**:
  If linked to a `MediaWork`:
  - If `MediaWork.IsCompleted` is `true`, count 100% of Total Characters.
  - Otherwise, count `MediaWork.CurrentCharactersRead`.
  If unlinked, 0.
- **Cover Image**:
  If linked to a `MediaWork` and `MediaWork.HasCover` is `true`, prefer the `MediaWork.CoverUrl` (reflecting any Google Books, OpenLibrary, TTSU, or user override cover). Otherwise, fallback to the installment's `CoverUrl` (from Jiten).
- **Status**:
  Completed (if linked and completed / 100%), In-Progress (if read > 0), or Not Started.

### 2. `MediaSeries` (Updates to Existing Entity)
- Has collection: `List<SeriesInstallment> Installments`.
- Optional `CoverUrl` (inherits from Jiten parent deck or first volume cover).
- Calculated properties / helper methods:
  - `TotalCharacters => Installments.Sum(i => i.EffectiveTotalCharacters)`
  - `CharactersRead => Installments.Sum(i => i.EffectiveCharactersRead)`
  - `ProgressPercentage => TotalCharacters == 0 ? 0 : Math.Min(100.0, (CharactersRead / (double)TotalCharacters) * 100.0)`
  - `CompletedInstallmentsCount => Installments.Count(i => i.IsCompleted)`

---

## Stage-by-Stage Roadmap

```mermaid
flowchart LR
    Stage1["Stage 1: Domain & DB"] --> Stage2["Stage 2: Series Creation"]
    Stage2 --> Stage3["Stage 3: Details & Linking"]
    Stage3 --> Stage4["Stage 4: Series Index"]
```

---

### Stage 1: Domain & Persistence Foundation
**Goal**: Create the `SeriesInstallment` entity, update `MediaSeries`, configure EF Core, and generate database migrations.

1. **Entity Definition**:
   - Create `Kiseki.Core.Entities.SeriesInstallment`.
   - Update `Kiseki.Core.Entities.MediaSeries` to hold `Installments`, `CoverUrl`, and progress aggregation logic.
2. **Database Context & Migrations**:
   - Register `SeriesInstallments` in `ImmersionDbContext`.
   - Configure relationships:
     - `MediaSeries -> SeriesInstallments` (Cascade delete: deleting series deletes its installment slots).
     - `SeriesInstallment -> MediaWork` (SetNull delete: deleting a library work simply unlinks the installment).
   - Generate EF migration: `AddSeriesInstallments`.
3. **Tests**:
   - Unit tests in `Kiseki.Tests` for `MediaSeries` and `SeriesInstallment` progress and character calculations.
   - Verify migration applies cleanly on test database.

---

### Stage 2: Series Creation (`/Series/Create`)
**Goal**: Allow users to create a series either from a Jiten parent deck with subdecks, or as a manual empty series.

1. **Create Form & Options**:
   - **Mode A: From Jiten Deck** (e.g. *Spice and Wolf*, *86*):
     - Search Jiten for a book series.
     - Selecting a deck fetches `GetDeckDetailAsync`.
     - Series is created with its title, Jiten deck ID, and installments populated from `detail.SubDecks`.
     - **Frictionless Auto-Matching**: For each installment, check the user's library for any `MediaWork` where `work.JitenSubdeckId == installment.JitenSubdeckId` (or matching deck ID). If found, link it immediately.
   - **Mode B: Manual / Empty Series** (e.g. *Aobuta* where each volume is a top-level deck):
     - User inputs Title and chooses Media Type (Book).
     - Creates empty series ready to have installments added on the details page.
2. **Tests**:
   - PageModel tests for `Create`: creating from Jiten subdecks, auto-matching existing works, and creating empty series.

---

### Stage 3: Series Details Page & Installment Management (`/Series/Details/{id}`)
**Goal**: View overall series progress, list all installments, and link/unlink/add installments.

1. **Overview Header**:
   - Series cover, title, overall progress bar (percentage + character count read / total).
   - Stats summary: e.g. "4 of 12 volumes completed".
2. **Installments List**:
   - Render each installment: sequence number, title, cover, character count, and progress badge.
   - If linked to a `MediaWork`: shows work title and link to view library work.
   - If unlinked: shows "Not in library" with a quick **"Link library work"** action.
3. **Actions**:
   - **Link / Unlink**: Associate an existing unlinked `MediaWork` from the library to this installment slot (or unlink it).
   - **Add Installment** (especially useful for manual series like *Aobuta*):
     - Modal or inline form to add a new volume slot (title, optional Jiten deck search/ID, optional character count).
   - **Remove Installment**: Remove an unneeded slot.
4. **Tests**:
   - PageModel tests for linking a work, unlinking, and adding an installment.

---

### Stage 4: Series Index (`/Series/Index`)
**Goal**: Present the library of series in a clean, visual grid/list.

1. **Series List/Cards**:
   - Card for each series showing cover, title, overall progress bar, and volume count (e.g. "3 / 8 volumes").
   - Click to open Series Details.
   - Empty state when no series exist yet (with button to "Create series").
2. **Tests**:
   - PageModel tests for `Index` querying and rendering series list.

---

## Next Immediate Step

Once you review and approve this plan, we will start with **Stage 1: Domain & Persistence Foundation**:
- Add `SeriesInstallment.cs`.
- Update `MediaSeries.cs`.
- Update `ImmersionDbContext.cs` & generate EF Core migration.
- Add unit tests verifying progress calculations.

