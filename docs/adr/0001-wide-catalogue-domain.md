# ADR 0001: Wide catalogue ownership, identity, and progress

- Status: Accepted
- Date: 2026-09-21
- Decision owners: Kiseki maintainers
- Scope: Batches 1A through 7B of the wide series/franchise/anime plan

## Context

`MediaWork` currently represents both a catalogue item and the user's tracked copy. That works for a library of individual books, but it cannot represent an untracked volume in a series, two editions of the same volume, or a reread without inflating series progress. `MediaSeries` and `Franchise` already exist, and legacy rows, logs, TTSU bindings, import receipts, covers, and Jiten metadata must remain intact.

This ADR records the defaults accepted by the maintainer for implementation. A later incompatible decision must supersede this record explicitly.

## Decision

### Catalogue and copy ownership

`MediaInstallment` is the canonical catalogue item. Use “installment” in internal names and a media-specific label such as “volume” or “season” in the UI. `MediaWork` remains the tracked copy/activity aggregate.

An installment has:

- a stable `Guid` identity and concurrency token;
- an explicit `MediaType`;
- an optional `MediaSeries` relationship;
- an independently stored order key and display label;
- a kind, release state, optional release date, and inclusion flag;
- canonical metadata with field-level provenance; and
- zero or more tracked copies.

An installment may be unassigned. This supports standalone books without inventing a series. When assigned, installment and series media types must match. A copy and its installment must also have the same media type.

Deleting a series sets installment membership to null and retains installments and copies. Deleting an installment that has copies is rejected. Moving copies and consolidating duplicate installments are explicit reviewed operations. Deleting a franchise continues to leave its series intact and unassigned.

Media-type changes on linked records go through a validated Core command. A type change must either preserve all type invariants or explicitly detach the affected relationship; direct mutation is not supported after Batch 1B.

### Backfill and provider identity

Batch 1A creates exactly one installment for every existing work and never merges rows by title. The initial installment uses the work `Guid` as its own `Guid`; this is deterministic and makes the backfill restart-safe. Every work ID, log ID, TTSU binding, receipt, override, cover, and legacy metadata value is preserved.

Provider identity belongs to an installment, not a copy. Its normalized key is unique per provider:

- Jiten standalone deck: `deck:{deckId}`
- Jiten child deck: `subdeck:{parentDeckId}:{subdeckId}`

The existing Jiten client fixtures establish that child membership is resolved from a freshly fetched parent detail page. A child ID is therefore interpreted in its parent context, so the composite child key is required even though the child DTO also exposes a deck ID. The backfill only creates a canonical identity when exactly one legacy work claims the normalized key. Duplicate claims remain on their copies as legacy evidence and are returned as review candidates; no claimant wins implicitly.

Matching titles never establish identity. Deck/child aliases and edition equivalence require an explicit reviewed association. Different provider editions remain separate unless a user reviews a consolidation. Consolidation moves copy relationships and retains their histories; it does not merge or delete copies or activity.

### Metadata ownership and provenance

Provider snapshots are stored separately from user corrections. A refresh may replace provider-owned snapshot values but must not overwrite user-owned fields.

| Field | Owner at migration | Refresh rule |
| --- | --- | --- |
| Copy title | Legacy unknown until edited | Never overwritten by catalogue refresh |
| Copy manual character override | User | Never overwritten |
| Copy TTSU total/bookmark | TTSU edition | Updated only by reviewed TTSU import |
| Copy legacy Jiten total/IDs | Legacy provider evidence | Retained until a dedicated cleanup migration |
| Copy cover with `LegacyUnknown`, `UserOverride`, or `Ttsu` source | Protected copy value | Never overwritten by provider refresh |
| Copy Google Books/Open Library/Jiten cover | Selected provider copy value | Preserved by migration; no automatic canonical inheritance |
| Installment provider title/count/release/cover | Provider snapshot | May change only through reviewed provider refresh |
| Installment title/order/release/inclusion correction | User | Survives refresh |

The effective installment value is the user correction when present, otherwise the current provider snapshot value, otherwise migrated legacy-unknown data. Cover resolution must expose whether the chosen image is exact, parent fallback, copy-local, or absent.

Copy character totals use this precedence:

1. manual override, including an explicit zero;
2. positive TTSU edition total;
3. positive compatible canonical Jiten total;
4. positive legacy copy Jiten total;
5. unknown.

Zero and negative totals are not valid denominators. An explicit zero override remains meaningful as “no usable total” and must not fall through to another source. A canonical total is compatible only when the copy is attached to that exact reviewed installment/edition.

### Order, release scope, and progress

Order is independent of display text. Seed backfilled order keys 100 apart within a series, using the existing stable work order followed by `Guid` as a tie-breaker. Reordering and any renumbering happen in one transaction. User-corrected title, order, inclusion, or release data survives refresh.

Headline series progress includes installments that are both included and confirmed released. Upcoming and excluded installments remain visible. Unknown release state remains visible and is reported separately; callers must choose an explicit scope rather than treating unknown as released or upcoming.

For a copy, explicit completion produces a fraction of 1. Otherwise its fraction is actual progress divided by a positive effective total, capped to `[0, 1]`; a missing total produces an unknown fraction. Reading to 100% and explicit completion remain distinct facts.

For an installment, any explicitly completed copy completes it. Otherwise select the greatest known copy fraction. Multiple copies never add to the catalogue denominator.

For included, released book installments with known canonical totals `C_i` and known progress, normalized series progress is:

`sum(C_i * p_i) / sum(C_i)`

where `p_i` is the selected copy fraction, or zero for an untracked installment. A tracked installment whose copy progress is unknown is excluded from the computable denominator and reported separately as unknown-progress canonical weight/count; it is not treated as zero. The numerator is labelled “estimated progress against known totals,” not lifetime characters read. Report canonical-total and computable-progress coverage separately. If there is no valid computable denominator, progress is unavailable rather than 0%.

Lifetime activity is the sum of actual sessions across copies. Franchise summaries retain the unit of each series and do not expose a cross-media percentage.

### Scope

Every provider mutation is previewed and confirmed against fresh server evidence. Manual anime progress is the first anime release. Manga, episode entities, watch-history import, and an anime provider are outside the accepted schema scope until their later discovery/decision batches.

## Acceptance examples

1. A standalone legacy book gets one unassigned installment and keeps its IDs, logs, TTSU state, cover, overrides, and provider evidence.
2. Two works with the same title get two installments. No title-based merge occurs.
3. Two works claiming `subdeck:10:11` get no canonical provider identity and appear as a duplicate-claim review case.
4. One installment with paperback and ebook copies contributes once to series progress; explicit completion on either copy completes it.
5. A manually completed copy with no total yields completed installment progress without fabricating lifetime characters.
6. An untracked released volume with a known canonical total contributes zero progress and remains in the denominator.
7. Upcoming, excluded, and unknown-release installments remain visible but only confirmed released/included entries enter the default headline denominator.
8. A protected TTSU cover and a selected Google Books cover both survive migration; later catalogue refresh does not rewrite either copy cover.
9. A franchise containing book and anime series shows their native units independently and no combined percentage.

## Consequences

The first migration is additive and temporarily keeps `MediaWork.MediaSeriesId` and legacy provider fields. Compatibility commands synchronize legacy series membership until all readers move to installments. Old binaries are not supported as writers after the new schema is deployed. Removal of legacy columns requires a separate migration and compatibility review.
