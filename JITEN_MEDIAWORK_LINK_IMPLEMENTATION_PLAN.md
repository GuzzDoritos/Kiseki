# Jiten MediaWork Linking and Library Details Implementation Plan

## Status

Implemented on September 4, 2026. The completed slice is covered by 35 passing automated tests and a successful Release build.

## Goal

Let a user open a work from `/Library`, inspect its real metadata and immersion history, click **Link to Jiten** beside **Edit**, search Jiten, choose a deck or subdeck, review the metadata that will be copied, and save the link.

The first slice synchronizes only metadata that Kiseki already uses:

- Jiten deck and optional subdeck IDs;
- Jiten character count;
- optionally, the `MediaWork.Title`;
- a Jiten cover URL;
- the existing effective progress derived from the work's immersion logs.

Descriptions, difficulty, vocabulary, genres, aliases, release dates, automatic matching, and background refresh are out of scope.

## Current repository state

Most of the underlying model is already present:

- `MediaWork` has `JitenDeckId`, `JitenSubdeckId`, `JitenCharacterCount`, `ManualCharacterCountOverride`, `Title`, `Logs`, and the `LinkToJitenDeck`, `LinkToJitenSubdeck`, and `RemoveJitenLink` methods.
- `TotalCharacters` already gives the manual override precedence over Jiten's count.
- `JitenDeckDTO` already deserializes the three Jiten titles, character count, `CoverName`, parent ID, and child count.
- `IJitenApiClient` and `JitenApiClient` already support book search and paginated deck details, and the Web project already registers the client.
- the Console application already supports selecting and linking a whole deck or a subdeck.
- each row on `/Library` already routes to `/Library/Details/{id}`.

The missing pieces are:

- the Web details and edit PageModels are presentation scaffolds and do not load data;
- there is no Web Jiten selection/linking flow;
- `MediaWork` has no place to persist a cover;
- Console-only `JitenMediaSelection` contains reusable mapping behavior that the Web project cannot currently share;
- the details page does not query or render immersion logs.

## Product decisions

### Initial media-type scope

The initial implementation supports `MediaType.Book` only. The current `SearchBooksAsync` implementation sends Jiten `mediaType=4` (Jiten's Novel type), and the Console explicitly rejects other Kiseki media types. On a non-book details page, omit the link button or show a disabled action with a short explanation.

Use generic names such as `JitenMediaSelection` and `LinkJitenModel` so a later change can map Kiseki's Anime and Game types to Jiten's broader media-type enum without replacing this flow.

### Title ownership

Treat the local title as user-owned. Linking must not rename a work silently.

The confirmation UI offers these choices, populated only when the corresponding Jiten value is non-empty:

1. Keep the current title — default.
2. Use Jiten's original title.
3. Use Jiten's English title.
4. Use Jiten's romaji title.

The POST sends only the title-choice enum. It must not post a title string that the server trusts. The handler resolves the selected Jiten deck again and derives the title from the authoritative response.

### Cover ownership

Add one nullable field to `MediaWork`, tentatively named `JitenCoverUrl`. No other new metadata columns are needed.

Jiten's current `DeckDto` exposes `CoverName`. Its current Web helper treats a non-empty value other than `nocover.jpg` as the image URL directly. Kiseki should therefore:

- convert null, blank, and `nocover.jpg` to `null`;
- accept only an absolute HTTPS URL for the persisted value;
- store the returned URL, not image bytes;
- load the image directly in the browser;
- render Kiseki's existing type-based placeholder when the URL is absent or the remote image fails;
- replace the stored URL when the work is relinked;
- clear it when `RemoveJitenLink` is called.

Do not call Jiten on every Library or Details request. Persisting the URL makes those pages local and fast. A later explicit **Refresh Jiten metadata** action can update a stale URL.

### Link semantics

For a whole-deck selection, persist that deck's ID, character count, title choice, and cover.

For a subdeck selection:

- persist the parent as `JitenDeckId`;
- persist the selected child as `JitenSubdeckId`;
- use the child character count;
- use the child title when a Jiten title is selected;
- prefer the child cover and fall back to the parent cover if the child has none.

Relinking replaces all Jiten-owned fields atomically. It must not modify `ManualCharacterCountOverride`, completion status, series assignment, or immersion logs.

## Intended user flow

```text
/Library
    -> click a MediaWork row
/Library/Details/{id}
    -> inspect progress, metadata, cover, and immersion sessions
    -> click Link to Jiten beside Edit
/Library/LinkJiten/{id}
    -> search is prefilled from the current MediaWork title
    -> search Jiten
    -> choose a whole deck or inspect and choose one of its subdecks
    -> choose whether Jiten should replace the local title
    -> confirm link
/Library/Details/{id}
    -> show the linked cover, IDs, effective character total, and success notice
```

No database changes occur during search or preview. Only the final POST mutates the tracked `MediaWork`.

## Stage 1: Extend the domain and database schema

### Files

- `Kiseki.Core/Entities/MediaWork.cs`
- `Kiseki.Core/ImmersionDbContext.cs`
- a new migration under `Kiseki.Core/Migrations/`
- `Kiseki.Core/Migrations/ImmersionDbContextModelSnapshot.cs`

### Changes

1. Add `string? JitenCoverUrl` to `MediaWork`.
2. Set a reasonable database maximum length, such as 2,048 characters.
3. Extend both link methods to accept the normalized cover URL and set it with the IDs and character count.
4. Extend `RemoveJitenLink` to clear the cover URL.
5. Generate a migration such as `AddJitenCoverUrlToMediaWorks` that adds a nullable `TEXT` column. Existing records require no backfill.
6. Keep the existing `TotalCharacters` behavior unchanged: `ManualCharacterCountOverride ?? JitenCharacterCount ?? 0`.

Prefer a domain method over setting Jiten fields independently in PageModels. The IDs, count, and cover represent one link and should change together.

### Acceptance criteria

- existing databases migrate without data loss;
- existing linked works receive a null cover and retain their IDs/counts;
- linking a whole deck or subdeck updates all Jiten-owned values together;
- unlinking clears IDs, count, and cover;
- a manual character override still controls `TotalCharacters` after linking.

## Stage 2: Share Jiten selection and normalization logic

### Files

- move or replace `Kiseki.Console/Models/JitenMediaSelection.cs`
- add a shared selection/value object under `Kiseki.Core`
- `Kiseki.Console/Screens/JitenLinkScreen.cs`
- `Kiseki.Core/DTOs/JitenDeckDTO.cs`, if helper placement requires it

### Suggested shape

Create a shared immutable value such as:

```csharp
public sealed record JitenMediaSelection(
    int DeckId,
    int? SubdeckId,
    string OriginalTitle,
    string RomajiTitle,
    string EnglishTitle,
    int CharacterCount,
    string? CoverUrl);
```

Give it either an `ApplyTo(MediaWork work, JitenTitleChoice titleChoice)` method or use a small Core mapper/service to apply it. Keep ASP.NET concerns out of Core.

Centralize these rules so Console and Web cannot drift:

- title fallback and title-choice resolution;
- `nocover.jpg` handling;
- absolute HTTPS cover validation;
- parent/subdeck ID mapping;
- child-cover fallback to parent cover.

Update the Console to use the shared type. This is behavior-preserving except that newly linked Console works can now retain a cover URL too.

### Acceptance criteria

- Console and Web apply the same link semantics;
- no Web project type is referenced by Core or Console;
- empty titles and placeholder covers never become persisted display values.

## Stage 3: Harden the Jiten client for the Web flow

### Files

- `Kiseki.Core/Services/IJitenApiClient.cs`
- `Kiseki.Core/Services/JitenApiClient.cs`
- `Kiseki.Tests/JitenApiClientTests.cs`

### Changes

1. Add optional `CancellationToken` parameters to search and detail calls and pass them through every paginated HTTP request.
2. Preserve the existing pagination behavior for search results and subdecks.
3. Ensure a missing or empty detail payload returns `null` and cannot be linked.
4. Keep calls user-triggered. Do not query Jiten when rendering Library or Details.
5. Translate common failures into actionable page errors:
   - network/timeout: Jiten could not be reached;
   - 429: Jiten rate limit reached; retry later;
   - malformed response: selected metadata could not be verified.
6. Set a bounded `HttpClient` timeout in Web configuration if the default is too long for an interactive request.

Jiten states that its public endpoints are rate-limited and unversioned. The implementation should tolerate failures without altering the local work.

### Server-side verification rule

Search results are previews, not trusted update commands. The final POST contains only:

- MediaWork ID from the route;
- parent deck ID;
- optional subdeck ID;
- title-choice enum.

The POST handler calls `GetDeckDetailAsync(parentDeckId)` and reconstructs the selection from `MainDeck` or the matching `SubDecks` item. Character count, titles, and cover must never be accepted from hidden form fields.

## Stage 4: Add the Web linking page

### Files

- new `Kiseki.Web/Pages/Library/LinkJiten.cshtml`
- new `Kiseki.Web/Pages/Library/LinkJiten.cshtml.cs`
- optional page-specific view models under `Kiseki.Web/Models/`
- `Kiseki.Web/wwwroot/css/site.css`

### Route and handlers

Use a route equivalent to `/Library/LinkJiten/{id:guid}`.

The PageModel should inject `ImmersionDbContext` and `IJitenApiClient` and provide:

- `OnGetAsync`: load the work, prefill its current title, and optionally perform a submitted search;
- a GET search path so searches are refreshable and do not need antiforgery state;
- `OnPostLinkAsync`: validate the work, re-fetch authoritative Jiten detail, apply the selection, save, and redirect;
- no POST handler that trusts client-provided title, count, or cover metadata.

Return `NotFound()` when the MediaWork does not exist. Enforce the initial book-only scope on both GET and POST, not only by hiding the UI.

### Search UI

The page should:

- show the current local title and a breadcrumb back to Details;
- prefill the query from `MediaWork.Title`;
- show loading-independent server-rendered results;
- display cover/placeholder, original title, English/romaji titles when present, character count, and subdeck count;
- offer **Link entire deck** for each parent result;
- offer **Choose a volume/subdeck** when `ChildrenDeckCount > 0` and then render the paginated detail result;
- show title choices beside the final confirmation action;
- preserve the query and selected parent when validation or API errors occur.

Use one POST form per final candidate or a single explicit confirmation panel. Razor Pages antiforgery protection remains enabled.

### Successful update

After `SaveChangesAsync`, set a TempData notice such as `Linked “...” to Jiten.` and redirect to `/Library/Details/{id}`. Post/Redirect/Get prevents an accidental repeat on refresh.

If the work was already linked, label the action **Change Jiten link** and make it clear that confirming replaces the current Jiten metadata.

## Stage 5: Make Library Details operational

### Files

- `Kiseki.Web/Pages/Library/Details.cshtml.cs`
- `Kiseki.Web/Pages/Library/Details.cshtml`
- new `Kiseki.Web/Models/MediaWorkDetailsViewModel.cs`, if a dedicated projection is preferred
- `Kiseki.Web/wwwroot/css/site.css`

### Data loading

Replace the ID-only scaffold with an asynchronous database query that loads:

- the requested `MediaWork`;
- its optional `MediaSeries`;
- all `ImmersionLog` records.

Use `AsNoTracking()` and return `NotFound()` for an unknown ID. Sort session rows by date descending and then by ID for deterministic ordering.

Keep display formatting in the Web view model rather than adding presentation concerns to the entities. The details model should expose at least:

- ID, title, media type, series title;
- linked deck/subdeck IDs and link state;
- cover URL;
- characters read, effective total, percentage, and completion state;
- manual override and Jiten character count separately so the user can understand the effective total;
- session count, total reading time, and session rows.

### Header and actions

Render the real media badge and title. Place actions together in the page header:

- **Edit**;
- **Link to Jiten** or **Change Jiten link**, immediately beside Edit for books.

The Edit page itself remains outside this plan; only its existing route/button is retained.

### Cover and progress

- show the persisted Jiten cover when present;
- use a type-based Kiseki placeholder otherwise;
- use `loading="lazy"`, meaningful alt text, and a no-referrer policy for the remote image;
- fall back visually if the remote URL stops working;
- feed `CurrentCharactersRead`, `TotalCharacters`, and `IsCompleted` into the existing `_ProgressBar` partial;
- label whether the total comes from a manual override or Jiten.

### Metadata

Show:

- series or `Unassigned`;
- Jiten state (`Not linked`, whole deck ID, or deck/subdeck IDs);
- Jiten character count when available;
- manual character override when present;
- completion/in-progress/not-started status.

Do not make a live Jiten request to render this card.

### Immersion logs

Replace the scaffold empty state with a responsive table or stacked list containing:

- date;
- characters read;
- time spent, formatted consistently from `TimeSpentMinutes`;
- source.

Display total sessions, total characters read, and total time above the history. Keep an informative empty state when there are no logs. The table should remain readable on narrow screens and should not hide zero-character sessions if they contain recorded time.

### Acceptance criteria

- clicking any Library row opens real data for that work;
- a missing work returns HTTP 404;
- progress matches the Library row for the same work;
- every stored immersion log is visible in deterministic order;
- linked metadata and cover render without contacting Jiten;
- the link action sits next to Edit and routes with the current work ID.

## Stage 6: Show linked covers in the Library list

### Files

- `Kiseki.Web/Models/MediaWorkListItemViewModel.cs`
- `Kiseki.Web/Pages/Library/Index.cshtml.cs`
- `Kiseki.Web/Pages/Shared/_MediaWorkRow.cshtml`
- `Kiseki.Web/wwwroot/css/site.css`

Add `JitenCoverUrl` to the list projection. When present, render the remote cover inside the existing `.media-cover` slot; otherwise preserve the current media-type tile. Keep the entire row clickable and ensure the image does not introduce a nested link or layout shift.

This stage uses only persisted metadata and must not add an N+1 API call pattern.

## Stage 7: Tests

### Domain tests

Extend `Kiseki.Tests/MediaWorkTests.cs` to cover:

- whole-deck link stores ID, count, and cover;
- subdeck link stores parent/child IDs and child metadata;
- relinking replaces old Jiten metadata;
- unlinking clears the cover with the other Jiten fields;
- manual character override still wins;
- title remains unchanged by default and changes only for an explicit valid title choice;
- placeholder or unsafe cover values normalize to null.

### Client tests

Extend `Kiseki.Tests/JitenApiClientTests.cs` using the existing stub HTTP handler:

- cover and all title variants deserialize from search and detail responses;
- cancellation propagates through pagination;
- missing detail data returns null;
- pagination still retrieves the selected subdeck;
- non-success and rate-limit responses do not produce a linkable selection.

No test should call the live Jiten service.

### Linking PageModel tests

Add `Kiseki.Tests/JitenLinkPageTests.cs` with SQLite in memory and a fake `IJitenApiClient`:

- unknown MediaWork returns `NotFound`;
- non-book work cannot be linked in the initial slice;
- search defaults to the current title and displays results;
- API errors preserve the local database state;
- whole-deck and subdeck POSTs persist the correct authoritative metadata;
- tampered posted title/count/cover values cannot affect the saved entity;
- keep-title is the default;
- each explicit title choice uses the corresponding server response;
- relinking replaces Jiten fields but preserves logs, series, manual override, and completion state;
- success redirects to the correct Details route and sets a notice.

### Details PageModel tests

Add `Kiseki.Tests/MediaWorkDetailsPageTests.cs`:

- existing work loads its series and all logs;
- logs are ordered newest first;
- aggregate characters/time and progress are correct;
- linked/unlinked metadata is represented correctly;
- unknown IDs return `NotFound`;
- a work with no logs produces the empty-state model.

### Migration test

Extend `DatabaseMigrationTests` to migrate a database from `20260903141537_AddFranchisesSeriesAndJitenSubdeckLinks` to the new migration and verify:

- existing works and logs remain intact;
- existing Jiten IDs/counts remain intact;
- `JitenCoverUrl` is null for pre-migration rows.

## Failure handling and safety

- Treat Jiten as unavailable rather than failing the whole application startup.
- Never save a partial link if the verification request fails.
- Respect request cancellation and use a bounded timeout.
- Keep search and detail calls user-triggered to remain comfortably inside Jiten's public rate limits.
- Do not proxy arbitrary image URLs through Kiseki in this slice.
- Accept only absolute HTTPS cover URLs returned by the verified Jiten response.
- Razor-encode all remote titles and use normal `img src` attribute encoding.
- Keep the final update behind an antiforgery-protected POST.
- Log enough server-side context to diagnose a failed deck ID without exposing stack traces in the page.

## Recommended implementation order

1. Add the cover field, domain invariants, migration, and migration/domain tests.
2. Move Jiten selection/normalization into Core and adapt Console.
3. Add cancellation and failure coverage to the API client.
4. Implement the LinkJiten PageModel and its tests.
5. Build the search/subdeck/confirmation Razor UI.
6. Replace the Details scaffold with the real query, progress, metadata, and log history.
7. Render persisted covers on Details and Library rows.
8. Run `dotnet test Kiseki.slnx` and a release build.
9. Manually verify link, relink, missing-cover, API-failure, 404, no-log, and many-log flows at desktop and mobile widths.

## Suggested commit breakdown

1. `Add persisted Jiten cover metadata`
2. `Share Jiten link selection across clients`
3. `Add MediaWork Jiten linking page`
4. `Connect Library details and immersion history`
5. `Display Jiten covers in the library`

Each commit should include its corresponding tests and leave the solution buildable.

## Definition of done

- a user can navigate from a Library row to a real Details page;
- Details displays the work's real title, type, series, progress, Jiten state, cover, and every immersion log;
- a book Details page has **Link to Jiten** beside **Edit**;
- search supports choosing a whole deck or subdeck;
- the final POST re-fetches and applies trusted Jiten metadata;
- character count and cover are always synchronized, while title replacement is explicit;
- linking never changes logs, manual override, series, or completion status;
- missing works and unavailable Jiten responses fail cleanly;
- Library and Details do not depend on a live Jiten call after metadata is saved;
- migrations preserve existing data;
- automated tests pass without using the live API.

## External contract references

- Jiten public API guide: <https://jiten.moe/guides/using-the-api>
- Jiten API and scraping policy: <https://jiten.moe/guides/api-and-scraping>
- Jiten `DeckDto` source, including `CoverName`, titles, and character count: <https://github.com/Sirush/Jiten/blob/master/Jiten.Api/Dtos/DeckDto.cs>
- Jiten cover display helper: <https://github.com/Sirush/Jiten/blob/master/Jiten.Web/app/utils/coverImage.ts>
- Jiten media-type enum: <https://github.com/Sirush/Jiten/blob/master/Jiten.Core/Data/MediaType.cs>
