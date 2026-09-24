# Wide series, franchise, and anime decisions

Status: Accepted by the maintainer on 2026-09-21

Date: 2026-09-21

Current checkout audited: `9b822e7` (the implementation remains the `8a12cb1` code baseline; the intervening commit adds Batch 0 documents only)

Detailed rationale and contract drafts: [ADR 0001](adr/0001-wide-catalogue-domain.md) and [Batch 0 audit](wide-series-franchise-anime-batch-0.md)

## Accepted product decisions

1. **Separate catalogue installments from tracked copies.** Add `MediaInstallment` between `MediaSeries` and `MediaWork`. An installment is a canonical volume, season, cour, movie, special, or game; a work remains one tracked copy/edition with its own history and source state.
2. **Allow standalone installments.** `MediaInstallment.MediaSeriesId` is nullable. Every legacy work can receive an installment without inventing a series. Assigned series/installment and installment/copy media types must match.
3. **Backfill without inference.** Create exactly one deterministic installment per existing work, initially reusing the work `Guid`. Never merge by title. Preserve all IDs, logs, bindings, receipts, overrides, covers, and legacy provider fields.
4. **Treat provider identity as canonical and unique only when unambiguous.** Store identity on the installment. Jiten keys are `deck:{deckId}` and `subdeck:{parentDeckId}:{subdeckId}`. If multiple legacy works claim a key, assign it to none and surface all claims for review. Consolidation moves copy relationships and never merges/deletes history.
5. **Separate provider snapshots from user corrections.** Refresh provider-owned title/count/release/cover fields through reviewed snapshots. Manual corrections live separately and survive refresh. Migrated legacy values remain explicitly legacy-unknown where ownership cannot be proved.
6. **Preserve copy metadata precedence.** Effective character total is explicit manual override (including zero), then positive TTSU edition total, then positive compatible canonical Jiten total, then positive legacy copy Jiten total, then unknown. A matching title alone never proves edition compatibility.
7. **Do not rewrite copy covers during migration or catalogue refresh.** `LegacyUnknown`, `UserOverride`, and `Ttsu` covers remain protected. Existing selected Jiten, Google Books, and Open Library covers also remain on the copy. Canonical cover inheritance is an explicit resolver that distinguishes exact, parent fallback, copy-local, and absent provenance.
8. **Store order separately from labels.** Seed order keys 100 apart with a stable `Guid` tie-breaker. Reorder/renumber in one transaction. User-corrected order, title, inclusion, and release values survive refresh.
9. **Keep unknown release state explicit.** Default headline progress includes only entries that are both included and confirmed released. Upcoming, excluded, and release-unknown entries remain visible. Unknown is reported separately and is never guessed into the denominator.
10. **Aggregate copies before installments, then installments before series.** Any explicitly completed copy completes its installment; otherwise use the greatest known copy fraction, capped at one. Multiple copies never increase the series denominator. Unknown totals produce unknown progress, not zero-length completion.
11. **Use canonical weighted book progress.** For released/included installments with known canonical totals and known copy progress, calculate `sum(C_i * p_i) / sum(C_i)`. An untracked installment has known `p_i = 0`. A tracked installment whose progress remains unknown (for example, an explicit zero copy override) is excluded from the computable denominator and reported as unknown-progress weight/count; it is not silently treated as zero. Label the numerator as estimated known-total progress, not lifetime characters. Report canonical-total coverage and computable-progress coverage separately; no computable denominator means unavailable, not 0%.
12. **Keep lifetime activity and media units honest.** Lifetime activity sums actual sessions across copies. Franchise summaries retain each series' unit and never combine characters, episodes, and time into one percentage.
13. **Make deletion and type changes explicit.** Deleting a series unassigns installments and retains copies. Deleting an installment with copies is rejected. Deleting a franchise unassigns series. Media-type changes use a Core command and either preserve type agreement or explicitly detach affected relationships.
14. **Make migration restart/concurrency safe.** Add schema and backfill before exposing catalogue writers. Serialize PostgreSQL backfill with a transaction-scoped advisory lock and SQLite backfill with an immediate database write lock; deterministic IDs, unique provider keys, idempotent inserts/upserts, and verification-before-commit remain the final safeguards. Concurrent-startup tests are required. Old binaries are not promised to be safe writers after deployment.
15. **Keep the first expansion scoped.** Wide books and manual franchises precede anime. Manual typed anime tracking precedes any provider client. Manga, episode entities, watch-history import, and automatic anime-provider selection remain out of scope.

## Acceptance examples

- A standalone legacy book gets an unassigned installment and retains every legacy value and ID.
- Two equal normalized titles become two installments, not an inferred merge.
- Two works claiming `subdeck:10:11` create no canonical identity and both appear in duplicate-claim review.
- A unique child claim creates `subdeck:{parent}:{child}` once; current client fixtures establish membership from the freshly fetched parent detail page.
- Paperback and ebook copies linked to one reviewed installment count once toward series progress and retain separate logs/totals/covers.
- An explicitly completed copy with no usable total completes the installment without fabricating lifetime characters.
- An untracked, released, included volume with a known canonical total contributes zero against that denominator.
- A tracked copy with an explicit zero total and partial activity contributes neither a guessed fraction nor a false zero; its canonical weight is reported as unknown-progress coverage.
- Upcoming, excluded, and release-unknown entries stay visible; only confirmed released/included entries enter the default headline.
- A protected TTSU cover and a selected external-provider cover both survive migration and later catalogue refresh.
- Deleting a series leaves its installments and copies intact and unassigned; deleting a populated installment is refused.
- Re-running or concurrently starting the upgrade produces one installment per legacy work and at most one canonical identity per normalized key.
- A franchise containing book and anime series presents each native unit independently and no combined percentage.

## Writer inventory

| Surface | Current path | Current mutation | Required boundary |
| --- | --- | --- | --- |
| Core persistent TTSU | `TtsuImportService.ApplyAsync` | Creates work/binding/logs, updates bookmark, total, completion, cover, optional Jiten metadata, receipt | Create/select installment inside the existing serializable transaction |
| Core convenience TTSU | `TtsuBookImporter.CreateMediaWork` / `MergeInto` | Creates/updates in-memory work and activity | Use shared copy/installment factory compatibility path |
| Core legacy aggregator | `MediaAggregator.ImportTtsuSessionsAsync` | Creates in-memory work/logs and optional Jiten link | Mark compatibility-only or route through shared factory |
| Console add | `AddMediaScreen.ImportFromJitenAsync` | Creates Jiten-linked work | Atomically create copy, installment, and reviewed identity |
| Console edit | `LibraryScreen.EditFieldAsync` | Direct title/type/total/completion/unlink mutations | Core copy/type/identity commands |
| Console series | `LibraryScreen.AssignSeriesAsync` | Creates series and assigns/unassigns work | Assign installment; mirror legacy work series during compatibility |
| Console Jiten | `LibraryScreen.LinkToJitenAsync` | Relinks work and optionally series parent | Canonical installment identity command after authoritative selection |
| Console delete | `LibraryScreen.DeleteAsync` | Deletes work and logs | Delete copy only; separate reviewed installment deletion |
| Web TTSU | `Import/Ttsu.OnPostConfirmAsync` | Builds reviewed request; Core persists | Preserve orchestration; add installment choice in Batch 4A |
| Web Jiten | `Library/LinkJiten.OnPostLinkAsync` | Refetches and relinks work | Invoke Core identity/link command |
| Web details | title/total/status/cover handlers | Direct copy scalar edits | Core copy commands with consistent concurrency/provenance |
| Web Series | Placeholder PageModels | No persistent writer | Implement only against Batch 2 command contracts |

## Query inventory

| Consumer | Current query | Planned rule |
| --- | --- | --- |
| Library index | No-tracking works with logs/series; bindings separately | Remains a tracked-copy list; project installment/series without adding catalogue-only rows |
| Library details | No-tracking work with logs/series plus binding | Add installment/canonical series; copy state remains authoritative for activity |
| Dashboard | Aggregate log queries and active work count | Remains activity/copy based |
| TTSU preview/matching | Book targets, binding, work/log/series state | Add installment identity/version and copy intent to reviewed fingerprint in Batch 4A |
| Console Library | Tracked works with logs/series | Include canonical installment/series while compatibility fields coexist |
| Series pages | Placeholder reads | Use bounded no-tracking projections; never load all session histories for cards |

All read-only queries use `AsNoTracking()`. Progress queries project every required total/activity input explicitly and do not rely on incidentally loaded navigations.

## Migration specification for Batch 1A

### Additive model

- Add `MediaInstallments`: deterministic `Guid`, nullable series FK, media type, explicit order key, kind, release state/date, inclusion, provider-derived metadata, separate user corrections, and concurrency token.
- Add installment provider identities with a unique `(Provider, NormalizedKey)` index and validated positive provider components.
- Add separate provider snapshots containing normalized fields, payload/fingerprint, observation time, completeness, and version.
- Add nullable `MediaWork.MediaInstallmentId` and copy concurrency state while retaining `MediaSeriesId` and all current copy metadata.
- Add indexes for series/order, copy/installment, identity lookup, and snapshot history; enforce nonnegative provider totals and valid enum/key values.
- Use `SetNull` for series-to-installment deletion, `Restrict` for installment-to-copy deletion, and cascade only identity/snapshot rows owned by an installment.

### Backfill transaction

1. Coordinate startup with a transaction-scoped PostgreSQL advisory lock or a SQLite immediate database write lock, and run schema/backfill before catalogue entry points become available. Lock keys/names are fixed constants, not derived from user input.
2. Insert one installment for each unlinked work using the work ID; copy media type, legacy series link, title as legacy-unknown, unknown release state, and included=true.
3. Seed order keys 100 apart per series using stable title then `Guid` ordering; never parse titles for canonical order.
4. Link each work to its deterministic installment and mirror its legacy series relationship during compatibility.
5. Normalize positive Jiten claims. Create identity/snapshot only for keys with one claimant; retain duplicated claims solely as copy evidence/review candidates.
6. Preserve copy counts, covers, provider IDs, overrides, bindings, logs, and receipts byte-for-value; do not reinterpret null or explicit zero.
7. Validate counts, IDs, FKs, media-type agreement, unique identities, and unchanged legacy values before commit. An invalid cross-type legacy link is reported and blocks catalogue exposure rather than being silently reassigned.
8. On retry, skip linked works and use deterministic/upserted provider state. Sequential and simultaneous reruns must create no duplicates.

### Provider-specific delivery gates

- Generate and review the PostgreSQL EF migration, SQL, and model snapshot with the repository-local tool.
- Extend `SqliteSchemaUpgrade` additively; never apply PostgreSQL migrations to SQLite or recreate a local database.
- Test fresh and physical old-schema SQLite files, repeat/interrupted upgrade, and concurrent startup.
- Run fresh/upgrade/concurrency/retry cases against disposable local PostgreSQL through `KISEKI_TEST_POSTGRES`; skipped PostgreSQL tests do not satisfy the gate.
- Fixtures cover standalone works, same-title works, duplicate/unique provider identities, multiple editions, every cover provenance, orphan logs, import state/receipts, mixed media, zero/unknown totals, and repeated/concurrent startup.

## Approval record

The maintainer accepted these choices before Batch 1A began. The highest-impact accepted choices are the nullable standalone installment, deterministic one-per-work backfill, duplicate provider claims receiving no winner, confirmed-released-only headline scope, greatest-copy-fraction aggregation, and preservation of all copy-level metadata during the additive migration.
