# Manual testing: TTSU statistics updates

Use a test database or a restorable copy of your library. The examples below use the synthetic book **Kiseki merge test** and do not need Jiten or Google Drive.

## Local setup

From the repository root, use a separate PowerShell session:

```powershell
$env:DatabaseProvider = 'sqlite'
$env:KISEKI_DB_PATH = Join-Path $PWD 'artifacts/manual-merge.db'
$env:KISEKI_PASSWORD = 'merge-test-only'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project Kiseki.Web --configuration Release --no-launch-profile --urls http://localhost:5187
```

Open `http://localhost:5187`, sign in with `merge-test-only`, and open Import → TTSU. Explicit environment variables override `.env`. An existing local SQLite database is upgraded additively at startup; it is never recreated to add the import metadata.

For local PostgreSQL testing, use a **test database**, set `DatabaseProvider=postgres`, and set `ConnectionStrings__DefaultConnection` to its Npgsql connection string or PostgreSQL URI. Run the same command. The generated EF migration runs at startup, just as on Render. Do not use the design-time factory's dummy connection for actual database operations.

## Repeatable import walkthrough

Each numbered folder under `docs/test-data/ttsu-merge` contains its own `ttu-reader-data` folder. In the browser folder picker, select the `ttu-reader-data` folder inside the scenario being tested. Keep the source book folder name unchanged between steps.

| Step | Folder | What to do and expect |
| --- | --- | --- |
| 1 | `01-initial` | Preview and confirm a new book. One day: 1,000 characters and 10 minutes on September 1, 2026. |
| 2 | `02-update` | Preview again. It should identify the existing book, show one changed day and one new day, and change the total to 5,000 characters / 50 minutes. Confirm; the library still has one work. |
| 3 | `02-update` again | Confirm the same export. No new or updated days; the total stays 5,000 / 50 minutes. |
| 4 | `01-initial` again | The older September 1 revision is skipped; September 2 is retained although absent from this file. The total stays 5,000 / 50 minutes. |
| 5 | `03-correction` | A newer September 1 revision reduces its daily total. Confirm and expect 4,500 characters / 45 minutes overall. September 2 remains. |
| 6 | `04-conflict` | September 1 has the same revision as step 5 but different values. Confirmation must require a decision. Choose **Keep stored**, select **Refresh review**, then confirm: 4,500 / 45 minutes. |
| 7 | `04-conflict` again | Choose **Use incoming baseline**, refresh the review, then confirm: 4,600 characters / 46 minutes. |

After each step, open the book details and dashboard to check the persisted reading totals. Daily TTSU statistics replace the previous daily snapshot; they are not additional sessions to sum. A book with no known character count can still show zero percent progress; inspect its reading totals rather than relying only on percentage.

## Existing library and conflict cases

- **Legacy records:** On a test copy of an older database, import the corresponding TTSU export. Existing rows have no source revision. Matching days that differ, or need a known baseline revision, ask which total to keep. Review before/after values, choose a baseline or keep the current value, refresh, and confirm. Existing work/log IDs and Jiten metadata must remain. Keeping a legacy value leaves its revision unknown, so later changed exports still require review.
- **Rename:** After an accepted import, rename the work using Console → Library (or choose a title while linking Jiten). Reimport the same source folder. The stored TTSU association should select the renamed work without creating another one.
- **Explicit target:** Choose a different existing target, then select Refresh review. The displayed current/resulting totals must switch to that work. Clicking Confirm immediately after changing a target refreshes the review without saving.
- **Duplicate titles/copies:** Choose Create a new copy and refresh before confirming. Upload it again; multiple source matches must require an explicit target. Only the chosen copy changes. Short work IDs distinguish identical titles in the selector.
- **Duplicate legacy days:** On a test database containing duplicate TTSU rows for a date, the preview lists each stored total and asks which to retain. Pick one, refresh, and confirm. Exactly one TTSU row remains for that date. Dates absent from the upload also require duplicate resolution. Manual logs on the same day remain separate.
- **Unassigned logs:** If legacy TTSU logs have no work, expand the assignment section on the intended existing book. Select only logs belonging to it and refresh. Resolve any overlapping days before confirming. A log cannot be assigned to two books in one batch.
- **Multiple files:** Put both initial and update JSON files in the same book folder, with filenames starting with `statistics`. Preview should use the union of dates and newest revision per day. Two files disagreeing at the same revision must require review. Same-title books in different folders remain separate preview entries.
- **Partial selection:** Deselect a book with unresolved conflicts. Other selected, resolved books should still import together.
- **Metadata preservation:** Link a test book to Jiten, set its manual character count/completion status through the existing Console controls, then update statistics. Title, link, cover, override, completion, and grouping must remain unchanged.

## Concurrent requests and expired previews

1. Open the same book's update preview in two tabs. Confirm in the first tab, then the second. The second must refresh its stale review instead of overwriting or adding duplicate daily rows.
2. Resubmit an already confirmed POST (for example, browser Back and resubmit). The committed operation receipt returns the original result. It does not repeat the import, even when its in-memory preview has been removed.
3. Restart the app while an unconfirmed preview is open, or leave it for more than 30 minutes. Confirming should explain that the preview expired and ask you to select the folder again. No reading data is changed.
4. Change a conflict choice and click Confirm without refreshing. The app should present the recalculated totals first; a subsequent confirmation applies the reviewed decision.

## Console

With the same database environment variables, run:

```powershell
dotnet run --project Kiseki.Console --configuration Release
```

Choose Book → Add book from TTSU folder and enter the full path to a scenario's `ttu-reader-data` directory. Select the book, choose an existing target or a new copy, review the daily table, resolve any conflicts, and confirm. Repeat steps 1–7 above. Web and Console should see the same persisted totals when configured for the same database.

## Render and Neon

Use a staging Render service and a separate Neon test database/branch for the first deployment. Keep `DatabaseProvider=postgres`, `ConnectionStrings__DefaultConnection`, and `KISEKI_PASSWORD` configured as in `render.yaml`; no Google credentials are needed. Before updating a real library, verify that you have a restorable Neon backup or branch.

1. Deploy the built commit to staging when ready. The Docker build publishes the Web project with the Core migration. Startup must complete `AddTtsuImportState` without recreating the database or rewriting existing statistics.
2. Sign in over HTTPS and run the same folder-upload steps from your computer. Files are uploaded by the browser; Render does not need access to your local folder path.
3. Restart/redeploy staging and reimport `02-update`. Reading data and matching associations must persist in Neon. Pending previews may expire on restart because they use memory; committed receipts are in PostgreSQL.
4. Let the Neon test compute idle, then reopen the app and import again. A successful retry must produce one committed result. A connection interruption must not produce partially imported books.
5. Repeat the two-tab test and verify the details/dashboard on staging.

The application currently keeps pending previews in one process. On multiple Render instances, use request affinity or expect to re-upload if a request reaches another instance. Durable statistics, bindings, and receipts are shared through PostgreSQL; durable distributed preview storage is outside this change.

## Automated verification

```powershell
dotnet test Kiseki.slnx
```

If a running local app locks Debug assemblies, use `--configuration Release` or stop that app first.

The PostgreSQL integration tests are opt-in and refuse remote hosts. They create and drop uniquely named test databases on a disposable local server; the supplied role needs database-creation permission:

```powershell
$env:KISEKI_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=YOUR_LOCAL_TEST_PASSWORD'
dotnet test Kiseki.slnx --configuration Release
```

These tests cover upgrade from the previous PostgreSQL migration, duplicate/orphan preservation, concurrent first imports, and a simulated lost commit response. They do not contact Neon, Jiten, or Google Drive. Clearing `KISEKI_TEST_POSTGRES` skips only the PostgreSQL integration tests.

TTSU's upstream [statistics documentation](https://github.com/ttu-ttu/ebook-reader#statistics-merge-mode) describes per-book/day tracking and revision-based merge behavior. Source paths/titles are matching hints, not globally stable identifiers; source renames or ambiguous matches still require selection.
