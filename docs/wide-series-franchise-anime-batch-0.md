# Wide catalogue Batch 0: audit, contracts, fixtures, and migration specification

Date: 2026-09-21

Audited baseline: `8a12cb1`

Decision record: [ADR 0001](adr/0001-wide-catalogue-domain.md)

## Exit assessment

Batch 0 is complete. The accepted domain decisions are recorded, the current writers and readers are inventoried, the dependent command/query contracts are frozen below, and the Batch 1A migration/backfill behavior is specified. The baseline suite passes with 674 tests passed, 4 PostgreSQL integration tests skipped, and 0 failed.

No import, cover, receipt-replay, or SQLite-upgrade correctness issue was found that blocks additive schema work. The skipped PostgreSQL tests mean PostgreSQL migration/concurrency readiness is not established; actual execution with `KISEKI_TEST_POSTGRES` is a mandatory Batch 1A exit gate.

## Baseline audit

The available history from the import reconciliation, metadata receipt, title-matching, external-cover, and TTSU folder-cover commits through `8a12cb1` was reviewed alongside the current code. The absent `automatic-import-implementation-plan.md` and older Stage 6A/6B prompt artifacts were not reconstructed or treated as evidence. `AUTOMATIC_IMPORT_DESIGN.md` is background design material; the executable behavior and tests summarized here are the baseline.

### TTSU review and commit boundary

- `TtsuDataLoader` validates source files and associates bookmark state within the source folder.
- `TtsuStatisticsNormalizer` and `TtsuProgressNormalizer` preserve ambiguous revisions for review.
- `TtsuMergePlanner` includes the target, source identity, binding, logs, resolutions, bookmark state, copy total, completion state, and source-match result in the reviewed fingerprint.
- `TtsuImportService.ApplyAsync` recomputes the preview inside a serializable transaction. A changed target, log, binding, source match, or reviewed resolution rejects the commit.
- New-source races are detected because source matching is recomputed for the transactional preview. Database uniqueness/serialization collisions return a committed receipt when one exists and otherwise require refreshed review.
- Orphan logs must still be orphaned TTSU rows at apply time. Duplicate daily rows and conflicting source revisions require an explicit winner and retain the chosen row ID.
- The Web batch keeps review tokens and candidate/cover choices server-side. Confirmation maps a token back to the server-held book and fingerprint, refetches Jiten metadata, and re-verifies an explicitly selected external cover.
- The in-memory batch cache currently uses a 20-minute sliding lifetime with a two-hour absolute cap. Expiration correctly requires re-upload. This differs from older narrative text that says 30 minutes; current code is authoritative and the duration is not a schema blocker.

### Receipt replay

- The Web batch ID is also the durable operation ID.
- `ApplyAsync` checks for an existing receipt before opening a transaction and on a collision after execution. A lost success response therefore returns the committed counts without applying requests twice.
- Receipts intentionally key idempotency by operation ID rather than by request payload. Batch IDs are server-generated and removed from cache after success; callers must never reuse one operation ID for a different logical import.
- Receipt rows contain aggregate result counts, not the full request. Installment choices added in Batch 4 must be included in the reviewed fingerprint; changing receipt semantics is not required for Batch 1A.

### Covers and metadata trust

- `MediaWork.IsCoverProtected` protects `LegacyUnknown`, `UserOverride`, and `Ttsu` values. Jiten application also preserves selected Google Books and Open Library covers.
- Jiten linking validates positive IDs, nonnegative counts, URL length/scheme, and cover provenance. Web POSTs refetch the selected parent/child relationship through `JitenSelectionResolver` instead of trusting hidden metadata.
- Google Books and Open Library cover application validates HTTPS, approved hosts, and URL-safe provider IDs. TTSU local cover paths are accepted through the dedicated TTSU method.
- TTSU folder covers may replace non-protected provider covers by current policy. Invalid optional covers are skipped without aborting reading-history import. Batch 1 migration preserves the resulting copy value and provenance exactly.
- Metadata enrichment is optional to reading import. If fresh Jiten resolution is unavailable at confirmation, activity may still commit without the proposed metadata; tests cover this deliberate behavior.

### Latest persistence upgrades

- PostgreSQL has seven ordered EF migrations through `AddOpenLibraryCoverSourceAndConstraint`; the model snapshot includes TTSU state, receipt counters, generalized cover provenance, and cover constraints.
- Existing local SQLite databases use `SqliteSchemaUpgrade`, not PostgreSQL migrations. The upgrade runs in a transaction, uses `CREATE ... IF NOT EXISTS` plus column inspection, and adds the bound-day unique index.
- Legacy `JitenCoverUrl` is renamed to `CoverUrl`, and existing non-null cover values become protected `LegacyUnknown` values. Provider item IDs and receipt counters are added without recreating the database.
- SQLite upgrades are tested for repeat execution and legacy-row preservation. PostgreSQL upgrade, concurrency, and lost-response tests exist but were skipped because no disposable server was configured.

## Writer inventory

These are all known paths that create a work or change series, type, provider identity, totals, completion, or covers. Batch 1B must route the starred relationship/identity writers through Core commands.

| Surface | Current writer | Mutations | Batch 1 action |
| --- | --- | --- | --- |
| Core persistent TTSU | `TtsuImportService.ApplyAsync` | Creates work, binding, logs, bookmark/total, completion, covers, optional Jiten link, receipt | Use tracked-copy creation/assignment command inside existing transaction; extend fingerprint later in Batch 4 |
| Core convenience TTSU | `TtsuBookImporter.CreateMediaWork` / `MergeInto` | Creates or updates in-memory work/logs/cover/total/completion | Create/select installment through the shared compatibility path |
| Core legacy aggregator | `MediaAggregator.ImportTtsuSessionsAsync` | Creates an in-memory work/logs and optional Jiten link | Mark compatibility-only or route through the shared factory; do not let it establish identity independently |
| Console add | `AddMediaScreen.ImportFromJitenAsync` | Creates work and applies Jiten selection | Create copy plus installment and canonical identity atomically |
| Console edit | `LibraryScreen.EditFieldAsync` | Direct title, media type, total, completion, unlink | Route type and identity changes through commands; scalar copy edits remain copy commands |
| Console series | `LibraryScreen.AssignSeriesAsync` | Creates series, assigns/unassigns work, seeds series Jiten ID | Assign the installment; mirror legacy `MediaSeriesId` during compatibility |
| Console Jiten | `LibraryScreen.LinkToJitenAsync` | Relinks work and may update series Jiten ID | Link canonical installment identity after authoritative selection |
| Console delete | `LibraryScreen.DeleteAsync` | Deletes logs and work | Delete copy only; retain empty installment unless separately deleted |
| Web TTSU | `Import/Ttsu.OnPostConfirmAsync` | Builds reviewed requests; persistent writes occur in Core | Keep PageModel orchestration; do not add direct catalogue writes |
| Web Jiten | `Library/LinkJiten.OnPostLinkAsync` | Authoritatively refetches and relinks work | Invoke Core identity/link command |
| Web details | title/total/status/cover POST handlers | Direct scalar work edits | Route through copy commands so concurrency and provenance are uniform |
| Series pages | Placeholders today | No persisted writes | Build only against Batch 2 contracts |

Direct public setters on `MediaWork.MediaType`, `MediaSeriesId`, `JitenDeckId`, `JitenCharacterCount`, and `MediaSeries.MediaType` are compatibility hazards. Batch 1B must stop supported UI/import paths from using them before legacy setters can be narrowed in a later cleanup.

## Query inventory

| Consumer | Current shape | Required compatibility change |
| --- | --- | --- |
| Web Library index | No-tracking works with logs and series; bindings queried separately | Continue to list copies only; project installment/series and progress inputs without changing list scope |
| Web Library details | No-tracking work with series/logs plus binding | Add installment and canonical series; keep copy totals and activity authoritative |
| Web dashboard | Aggregate log queries plus active work count | No catalogue-count substitution; lifetime activity remains copy/log based |
| Web TTSU preview | Targets books; loads copy/log/series/binding state | Include installment identity/version and assignment in Batch 4 fingerprints |
| Console Library | Tracked works with logs and series | Include installment and canonical series while compatibility columns coexist |
| TTSU target matching | No-tracking book targets and bindings | Continue copy matching; persisted binding precedes title matching |
| Series pages | Placeholder models | Use only the frozen Batch 2 query records below; bounded projections, no whole-library log graph |

Read-only queries remain `AsNoTracking()`. Any query calculating copy progress must project the effective total inputs or include logs; it must not depend on a navigation being incidentally loaded.

## Frozen Core contract shapes

These are normative API shapes for Batches 1B and 2A. Namespace placement and documentation comments may vary, but UI work must not add fields or reinterpret behavior without returning a contract change to the Core owner. `ExpectedVersion` is required for updates; all results return the new version.

```csharp
public sealed record CreateTrackedCopyCommand(
    string Title,
    MediaType MediaType,
    Guid? InstallmentId,
    Guid? SeriesId,
    ProviderIdentityInput? ProviderIdentity,
    CopySeedMetadata? CopyMetadata);

public sealed record AssignCopyToInstallmentCommand(
    Guid CopyId,
    Guid? InstallmentId,
    Guid ExpectedCopyVersion);

public sealed record LinkInstallmentProviderCommand(
    Guid InstallmentId,
    ProviderIdentityInput Identity,
    ProviderSnapshotInput Snapshot,
    Guid ExpectedInstallmentVersion);

public sealed record ChangeCopyMediaTypeCommand(
    Guid CopyId,
    MediaType MediaType,
    bool DetachFromInstallment,
    Guid ExpectedCopyVersion);

public sealed record TrackedCopyCommandResult(
    Guid CopyId,
    Guid InstallmentId,
    Guid? SeriesId,
    Guid CopyVersion,
    Guid InstallmentVersion);

public sealed record ProviderIdentityInput(
    string Provider,
    string NormalizedKey);

public sealed record CopySeedMetadata(
    int? LegacyJitenDeckId,
    int? LegacyJitenSubdeckId,
    int? LegacyJitenCharacterCount,
    int? ManualCharacterCountOverride,
    string? CoverUrl,
    MediaCoverSource CoverSource,
    string? CoverProviderItemId);
```

```csharp
public sealed record CreateSeriesCommand(string Title, MediaType MediaType);
public sealed record EditSeriesCommand(Guid SeriesId, string Title, Guid ExpectedVersion);

public sealed record AddInstallmentCommand(
    Guid? SeriesId,
    MediaType MediaType,
    string DisplayLabel,
    InstallmentKind Kind,
    ReleaseState ReleaseState,
    DateOnly? ReleaseDate,
    bool IsIncluded,
    int? BeforeOrderKey);

public sealed record EditInstallmentCommand(
    Guid InstallmentId,
    string? DisplayLabelOverride,
    InstallmentKind Kind,
    ReleaseState? ReleaseStateOverride,
    DateOnly? ReleaseDateOverride,
    bool IsIncluded,
    Guid ExpectedVersion);

public sealed record MoveInstallmentCommand(
    Guid InstallmentId,
    Guid? SeriesId,
    int? BeforeOrderKey,
    Guid ExpectedVersion);

public sealed record ConsolidateInstallmentsCommand(
    Guid SourceInstallmentId,
    Guid TargetInstallmentId,
    Guid ExpectedSourceVersion,
    Guid ExpectedTargetVersion);

public sealed record CatalogueCommandResult(
    Guid EntityId,
    Guid Version,
    IReadOnlyList<Guid> AffectedCopyIds);
```

```csharp
public sealed record SeriesListItemResult(
    Guid SeriesId,
    string Title,
    MediaType MediaType,
    int IncludedReleasedCount,
    int CompletedCount,
    int TrackedCount,
    int CatalogueOnlyCount,
    int UpcomingOrExcludedCount,
    int UnknownReleaseCount,
    ProgressSummaryResult Progress,
    Guid Version);

public sealed record SeriesDetailsResult(
    Guid SeriesId,
    string Title,
    MediaType MediaType,
    ProgressSummaryResult Progress,
    ActivitySummaryResult LifetimeActivity,
    IReadOnlyList<InstallmentResult> Installments,
    Guid Version);

public sealed record InstallmentResult(
    Guid InstallmentId,
    int OrderKey,
    string DisplayLabel,
    InstallmentKind Kind,
    ReleaseState ReleaseState,
    DateOnly? ReleaseDate,
    bool IsIncluded,
    int CopyCount,
    bool IsExplicitlyCompleted,
    double? SelectedCopyFraction,
    int? CanonicalTotal,
    MetadataCoverageResult Metadata,
    IReadOnlyList<TrackedCopySummaryResult> Copies,
    Guid Version);

public sealed record ProgressSummaryResult(
    double? Fraction,
    long? EstimatedKnownTotalProgress,
    long KnownTotalDenominator,
    int KnownTotalInstallments,
    int UnknownTotalInstallments,
    int UnknownProgressInstallments);
```

Command implementations return typed validation, not-found, concurrency-conflict, duplicate-identity, and delete-rejected outcomes rather than presentation strings. Provider preview/apply and TTSU installment-choice records remain deferred to Batches 3A and 4A because those shapes depend on their planners; UI for those batches must wait for those records.

## Batch 1A schema and backfill specification

### Additive tables and columns

1. Add `MediaInstallments` with `Id`, nullable `MediaSeriesId`, `MediaType`, `OrderKey`, `Kind`, provider-derived metadata fields, separate nullable user-correction fields, `IsIncluded`, and a `Version` concurrency token.
2. Add `InstallmentProviderIdentities` with `MediaInstallmentId`, provider, normalized key, provider item components, and a unique `(Provider, NormalizedKey)` index.
3. Add `InstallmentProviderSnapshots` with identity FK, normalized provider fields, raw/sanitized snapshot payload, fingerprint, observed timestamp, completeness marker, and version. Snapshot values never occupy manual-correction columns.
4. Add nullable `MediaWorks.MediaInstallmentId` and a copy concurrency token. Retain `MediaWorks.MediaSeriesId` and all legacy Jiten/cover/count fields.
5. Add indexes for installment series/order, copy installment, provider identity lookup, and snapshot identity/time. Add checks for positive provider IDs, nonnegative provider totals, valid release/order enums, and identity-key format/length.
6. Configure series deletion to set installment series null, installment deletion to restrict while copies exist, and identity/snapshot deletion to cascade from an installment identity only.

Cross-row media-type agreement is enforced by Core commands and verified after backfill; EF check constraints cannot reference the related row. Catalogue entry points remain disabled if verification reports an inconsistency.

### Transactional backfill

1. Acquire the provider-appropriate migration lock and run the schema change and backfill under the startup migration transaction/coordination mechanism.
2. For each work with no installment link, insert an installment whose `Id` equals the work `Id`, copying media type, legacy series membership, title as legacy-unknown display data, release as unknown, and inclusion as true.
3. Seed order keys 100 apart per series from the current deterministic ordering (title, then work `Guid`). Standalone entries receive a stable default key. Do not derive order by parsing titles.
4. Set `MediaWork.MediaInstallmentId` to its own ID. Mirror the existing series relationship during compatibility.
5. Normalize every positive legacy Jiten claim. Insert an identity and initial provider snapshot only when the normalized key has exactly one claimant. Leave duplicate claims unclaimed and queryable from the retained work columns.
6. Copy cover/count metadata into provenance/snapshot fields without modifying the copy fields. Preserve null, explicit zero manual overrides, unknown covers, and provider item IDs exactly.
7. Verify row counts, deterministic links, FK targets, copy/installment media types, installment/series media types, unique identities, and unchanged IDs/values before commit.

Rerunning the backfill skips already linked works and uses deterministic IDs/upserts for provider state. It must create no additional installment, identity, or snapshot rows. Concurrent startup is tested as a real race, not only sequential replay.

### SQLite and PostgreSQL delivery

- Generate the authoritative PostgreSQL migration with the repository-local `dotnet-ef` tool and review its SQL and model snapshot.
- Extend `SqliteSchemaUpgrade` additively. Do not apply PostgreSQL migrations to SQLite and do not recreate a local database.
- Test fresh PostgreSQL migration, upgrade from the last schema, interrupted/retried startup, and concurrent startup against a disposable local server.
- Test a physical legacy SQLite file, fresh SQLite creation, repeated upgrade, and interruption recovery. `EnsureCreatedAsync` alone is not upgrade coverage.
- Assert pre/post IDs, row counts, logs, binding/bookmark state, receipt counts, overrides, all cover fields, and legacy provider fields. Never point tests at the user's database.

## Representative fixture matrix

| Fixture | Minimum data | Required assertion |
| --- | --- | --- |
| Standalone legacy book | Work, logs, no series/provider | Deterministic unassigned installment; every legacy value unchanged |
| Same-title books | Two works with equal normalized titles | Two installments; no inferred relationship |
| Duplicate provider claim | Two works with the same Jiten deck or parent/child key | No canonical identity; both legacy claims retained and reviewable |
| Unique standalone deck | One work with positive deck ID | `deck:{id}` identity and snapshot created once |
| Unique child deck | Parent ID plus child ID | `subdeck:{parent}:{child}` identity and parent relationship retained |
| Multiple editions/copies | Two works in one legacy series with distinct logs/totals/covers | Separate initial installments; later reviewed consolidation can move both copies without merging history |
| Unknown/protected covers | Null, legacy, manual, TTSU, Jiten, Google, Open Library examples | Exact URL/source/provider ID preservation; no refresh inheritance |
| Orphan activity | TTSU and non-TTSU logs with null work IDs | Rows remain orphaned and unchanged |
| Import state | Binding, bookmark, bound daily rows, receipt | IDs, unique bound days, revisions, totals, and replay counts unchanged |
| Mixed media series set | Book/anime/game series and standalone works | Each record keeps its media type; invalid cross-type legacy links are reported without silent reassignment |
| Explicit zero/unknown totals | Zero manual override, null totals, positive TTSU/Jiten totals | Zero remains explicit; only positive compatible totals become denominators |
| Retry/concurrent startup | Upgrade invoked repeatedly and simultaneously | One installment per work and one unambiguous identity per key |

## Deferred, non-blocking work

- Optional cover-provider expansion is separate from the catalogue schema.
- PostgreSQL execution requires the disposable `KISEKI_TEST_POSTGRES` environment and is a Batch 1A release gate.
- Provider refresh contracts, TTSU installment choices, franchise graph review, typed anime activity, and provider selection remain in their assigned later batches.
