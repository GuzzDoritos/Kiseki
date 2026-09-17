# Gemini implementation prompt: automatic TTSU metadata enrichment

Paste the prompt below into Gemini Flash 2.8 High. Change `TARGET_STAGE` only after the preceding stage has been completed and verified.

---

You are implementing automatic TTSU metadata enrichment in the Kiseki repository.

`TARGET_STAGE = 0`

First read these files completely:

1. `AGENTS.md`
2. `docs/automatic-import-implementation-plan.md`
3. `AUTOMATIC_IMPORT_DESIGN.md`

Treat `docs/automatic-import-implementation-plan.md` as the repository-grounded implementation plan. Treat `AUTOMATIC_IMPORT_DESIGN.md` only as product direction where it does not conflict with the implementation plan or current code.

Then inspect the current implementations and tests named by the target stage. Do not assume the plan's suggested types or signatures already exist; earlier stages may have changed the repository.

## Assignment

Implement **only TARGET_STAGE** from `docs/automatic-import-implementation-plan.md`, including all production code, refactors, tests, dependency registration, and generated migrations required by that stage.

Do not start later stages. Do not leave unused scaffolding for later stages. If the target stage exposes an existing bug directly within its stated scope, fix it and cover it with a regression test. Do not broaden the feature.

## Non-negotiable constraints

- Extend the existing TTSU import pipeline. Do not create a second import workflow, controller, transaction, or persistent preview system.
- Preserve the server-held batch, reviewed fingerprint, refresh-before-confirm, serializable commit, operation receipt, and replay protections.
- Reading history is primary. Metadata uncertainty or Jiten availability must never corrupt reading data.
- Never trust posted Jiten titles, counts, covers, confidence scores, enum values, IDs, GUIDs, or candidate keys.
- A work representing one volume must never link to a Jiten parent deck that has child decks.
- Never overwrite an existing Jiten link or existing cover during automatic enrichment.
- Never replace the TTSU title automatically.
- Never overwrite `ManualCharacterCountOverride`, `TtsuCharacterCount`, completion state, grouping, or logs while applying Jiten metadata.
- Preserve `TotalCharacters = ManualCharacterCountOverride ?? TtsuCharacterCount ?? JitenCharacterCount ?? 0`.
- Never use daily characters read as a book-length estimate.
- Do not perform external HTTP inside a database transaction or EF execution-strategy callback.
- Pass cancellation tokens through all async EF and HTTP calls. Propagate request cancellation rather than presenting it as a recoverable Jiten outage.
- Use `AsNoTracking()` for read-only EF queries and tracked entities for mutations.
- Tests must use stubs/fakes. They must not call live Jiten, retailer, CDN, or other external endpoints.
- Do not scrape Amazon Japan or BookWalker.
- Do not implement Stage 6 unless `TARGET_STAGE` is explicitly set to 6 and the provider, usage, and storage prerequisites in the plan have already been decided by the user.
- Preserve unrelated user changes in the working tree. Do not reset, discard, or rewrite them.

## Implementation method

1. Inspect `git status --short` before editing.
2. Read every current file that the target stage will modify and the relevant existing tests.
3. Summarize the target stage in a short checklist before coding.
4. Implement the smallest complete version of that stage.
5. Prefer typed result records and small pure methods over boolean flags or business rules in Razor markup/PageModels.
6. Keep Core independent of ASP.NET and front-end concerns.
7. Update existing call sites rather than adding compatibility paths that duplicate behavior.
8. Add tests in the same change as each behavior.
9. If an entity/schema changes, generate the migration using:

   ```powershell
   dotnet tool run dotnet-ef migrations add <MigrationName> --project Kiseki.Core --startup-project Kiseki.Core
   ```

   Also update `SqliteSchemaUpgrade` when the plan requires compatibility with existing local SQLite databases. Do not hand-edit generated migration designer or snapshot files unless the EF tool cannot run and the user explicitly authorizes that fallback.

10. Run focused tests while iterating, then run:

    ```powershell
    dotnet test Kiseki.slnx
    ```

11. Run `git diff --check` and inspect the final diff for accidental scope expansion.

## Handling ambiguity

- Resolve ordinary implementation details by following existing repository patterns and the staged plan.
- If a decision would change the architecture, persistence model, external-provider policy, or safety guarantees beyond the target stage, stop and ask one precise question before making that change.
- If a test reveals that the plan is incompatible with current code, explain the concrete conflict and propose the smallest safe adjustment. Do not silently redesign the system.
- Do not weaken a safety gate merely to produce more automatic matches. Returning `Review`, `None`, or importing without metadata is an acceptable result.

## Completion report

When TARGET_STAGE is complete, report:

- the behavior implemented;
- the important files changed;
- migrations generated, if any;
- tests added or updated;
- the exact verification commands and results;
- any remaining risks or decisions for the next stage.

Do not claim completion if the full test suite fails or required work from TARGET_STAGE remains. Stop after the target stage and wait for review before proceeding.

---

For the first run, leave `TARGET_STAGE = 0`. After reviewing and committing that result, change it to `1`, then repeat through Stage 5. Stage 6 intentionally requires separate product and provider decisions.
