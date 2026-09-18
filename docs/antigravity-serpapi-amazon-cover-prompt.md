# Antigravity implementation prompt: Amazon cover discovery through SerpAPI

You are implementing the next focused repair in the Kiseki repository. Read `AGENTS.md` completely before changing anything, inspect the current implementation, and preserve all established domain and import invariants.

This is an implementation task, not a speculative design task. First confirm the current code paths and tests described below, then implement the smallest coherent provider-neutral change, generate any required EF migration with the repository-local tool, and run the complete verification suite.

## Current repository state

The preceding title-matching and cover repair is already implemented in commit `624ffb1` (`feat(core,web): repair title matching and add Google Books and Open Library cover providers`). Do not redo or revert that work.

Important existing behavior includes:

- TTSU title parsing, suffix cleanup, bounded search aliases, explicit/implicit volume reasoning, and branch/qualifier safety.
- Jiten remains the metadata and identity source. External image providers supply covers only.
- Google Books cover lookup and compact manual edition selection.
- Open Library ISBN cover lookup and persisted `MediaCoverSource.OpenLibrary = 6`.
- Server-side review tokens and confirmation-time provider revalidation.
- Cover URL/source consistency constraints and provider item provenance.
- A shared image validator with host allowlists, redirect restrictions, byte limits, actual raster inspection, dimension checks, and portrait-cover checks.
- Optional cover enrichment must never make an otherwise valid TTSU/Jiten import fail.
- Bulk lookup progress is shown in the import UI.

The real-world result of the preceding implementation is that title/Jiten matching is improved, but Google Books frequently has no usable Japanese light-novel image and Open Library contributes almost nothing for these editions. Amazon Japan normally has the desired cover. Amazon's own search/scraper did not return useful results for a title such as:

```text
Re：ゼロから始める異世界生活 13
```

The same query in Google Images through SerpAPI returned the correct Amazon cover as the first result, including the Amazon image URL. Implement Amazon cover discovery through SerpAPI's Google Images endpoint.

Official references:

- Google Images API: <https://serpapi.com/google-images-api>
- Image result schema: <https://serpapi.com/images-results>
- Endpoint: `https://serpapi.com/search.json?engine=google_images`

Relevant `images_results` fields include `position`, `source`, `title`, `link`, `original`, `original_width`, `original_height`, and `is_product`. Use `original` as the candidate image, never the SerpAPI thumbnail.

## Goal

For a strongly identified Jiten book candidate, discover the correct volume-specific Amazon Japan cover through SerpAPI, prefer it over the weaker automatic sources, show no more than three compact choices when human review is genuinely required, and persist truthful provenance.

The expected provider order for **new cover resolution** is:

1. Existing user/manual cover override remains protected and is never replaced.
2. Exact Amazon Japan cover discovered through SerpAPI.
3. Exact Google Books cover.
4. Existing Jiten volume-specific or parent/series fallback.

Open Library must no longer run during new automatic/bulk cover resolution. Retain its enum value, migration history, domain support, attribution behavior, and confirmation support for already reviewed or persisted Open Library selections. Do not delete or rewrite old migrations and do not invalidate existing records. It may remain as a dormant compatibility provider, but it must not consume network calls in ordinary new imports.

## Non-negotiable identity boundary

SerpAPI/Google Images is not a book metadata authority. It must not select a Jiten deck, change a title, infer character counts, or make a weak Jiten candidate appear authoritative.

Only attempt Amazon cover discovery after the import has a selected or high-confidence, exact Jiten title-and-volume candidate under the existing safety rules. Feed the search service the already parsed TTSU title, canonical Jiten parent/standalone title, exact structured volume, and branch qualifiers. Never search every Jiten candidate.

An attractive first image is not sufficient proof. Result position may be used only as a stable tie-breaker after identity gates; it must never be the reason a result passes.

## Provider-neutral boundary

Do not put SerpAPI behavior inside `GoogleBooksCoverService`, and do not add more provider-specific branching directly to Razor markup.

Introduce or finish a small provider-neutral orchestration boundary in Core for:

- resolving a cover using the configured provider precedence;
- returning a generic cover match/result and up to three generic review options;
- verifying a reviewed provider item during confirmation; and
- routing to the provider-specific SerpAPI/Amazon and Google Books services.

Use clear provider-neutral names for new shared models. If renaming an existing Google-specific shared model such as `GoogleCoverLookupContext`, `GoogleBooksCoverMatchResult`, or fields named `GoogleCover` is needed to keep the code honest and comprehensible, make that bounded refactor and update its tests. Do not gratuitously rewrite the proven Google matcher/client. Provider-specific HTTP clients, DTOs, matching rules, and configuration should remain in their own Core service namespaces.

Business rules and matching belong in `Kiseki.Core`, not in `Ttsu.cshtml`, JavaScript, or a Razor PageModel. The web layer should coordinate the workflow and project generic results to view models.

## SerpAPI configuration and client

Add server-side configuration using this shape:

```text
SerpApi:ApiKey
```

Environment-variable form:

```text
SerpApi__ApiKey
```

Requirements:

- Add a blank documented entry to `.env.example`.
- Add `SerpApi__ApiKey` as a non-synchronized secret in `render.yaml`.
- Do not place a real key in source, appsettings, tests, logs, HTML, JavaScript, batch state returned to the browser, exception messages, or committed files.
- Treat a missing/blank key as `NotConfigured`; fall through to Google Books without presenting it as an import error.
- Use a typed `HttpClient`, explicit timeout, cancellation tokens, and friendly mappings for 401/403, 429, 5xx, timeout, malformed JSON, and transport failures.
- The API key must be sent only to the exact HTTPS SerpAPI host. Do not follow endpoint redirects to another host.
- Because SerpAPI uses an `api_key` request parameter, ensure request URLs are not logged. Do not log complete request URIs or query strings.
- Do not send a request when no eligible exact title/volume search can be built.

Add minimal DTOs for only the response fields the implementation consumes. Missing arrays or fields must be handled safely.

## Bounded search behavior

The normal automatic path should issue at most one SerpAPI Google Images search per eligible book. Build the query from the canonical Japanese Jiten title plus the explicit normalized volume and constrain it to Amazon Japan, for example:

```text
Re：ゼロから始める異世界生活 13 site:amazon.co.jp
```

Use these request parameters unless current official API behavior proves a parameter unsuitable:

```text
engine=google_images
google_domain=google.co.jp
gl=jp
hl=ja
safe=active
imgar=t
imgsz=l
```

Rules:

- Reuse the existing Unicode normalization and `StructuredVolume` behavior. Arabic, Japanese/full-width, and safe attached-volume forms must converge on the same search volume.
- Do not put the entire title in mandatory double quotes; Amazon result titles often contain legal retailer/imprint wrappers.
- Do not include unrelated TTSU suffix noise already removed by `MediaTitleParser`.
- Do not generate an unbounded sequence of query variants.
- If there is no canonical Japanese title, do not guess from a weak English/romaji resemblance in the automatic path. Fall back to the existing providers.
- Cache/deduplicate identical normalized queries within the import batch/service lifetime so review refreshes do not fan out duplicate calls. Failed, canceled, and malformed requests must not poison the cache permanently.
- Bound SerpAPI concurrency to at most two requests. Respect cancellation and 429 responses. Do not retry 429 in a tight loop.

The free SerpAPI allowance is small enough that request discipline matters: one 41-book import should be approximately 41 searches, not one search per candidate or several variants per book.

## Strict Amazon result filtering

Inspect only a bounded leading result window (for example, the first 10 image results), then reject aggressively.

A result is eligible only when all of the following are true:

1. `link` is an absolute HTTPS Amazon Japan product URL whose host is exactly `amazon.co.jp` or `www.amazon.co.jp`.
2. A valid 10-character Amazon ASIN can be extracted from an approved product path such as `/dp/{ASIN}` or `/gp/product/{ASIN}`. Normalize it to uppercase and use it as the durable provider item ID.
3. `original` is an absolute HTTPS URL on an explicitly allowlisted Amazon image host. Start with exact hosts actually represented by fixtures/observed results, such as `m.media-amazon.com` and, only if needed by real fixtures, `images-na.ssl-images-amazon.com`. Do not allow arbitrary `*.amazon.com` suffix matching.
4. The result title, after safe retailer/format wrapper cleanup through shared title parsing, matches the expected canonical series/base title.
5. The result title proves the exact requested volume. Normalize `13`, `１３`, `第13巻`, `13巻`, and other forms already supported by `StructuredVolume`. Never silently accept a markerless result for volume 13.
6. Branch qualifiers are compatible. Reject manga/comic, anthology/short-story, EX, artbook, omnibus/complete-set, magazine, audiobook, drama CD, and other conflicting product branches unless the request explicitly has that same branch.
7. The candidate image passes the existing hardened image validator using the Amazon-host allowlist. Actual fetched bytes and dimensions are authoritative; SerpAPI's `original_width`/`original_height` are hints only.
8. The image is a plausible portrait book cover and meets the existing non-thumbnail threshold. Do not rewrite undocumented Amazon CDN URL transformations or guess a larger URL.

Do not accept a non-Amazon source page merely because it hotlinks an Amazon CDN image. The Amazon product page plus ASIN is the required identity/provenance pair.

Canonicalize the attribution link to a safe form such as:

```text
https://www.amazon.co.jp/dp/{ASIN}
```

Strip affiliate, tracking, session, and search query parameters before persistence or display.

## Deduplication and automatic selection

- Collapse duplicate results with the same ASIN.
- Also collapse identical validated cover assets returned more than once for the same product.
- If exactly one eligible exact-volume Amazon cover remains, select it automatically.
- If several eligible results reduce to the same validated image, treat them as one logical cover and keep one canonical ASIN/attribution record deterministically.
- If two or more genuinely different exact Amazon covers remain, do not choose based solely on result position. Return at most three review options, sorted deterministically by identity quality, actual pixel area, then original result position.
- If nothing survives, fall through to Google Books. SerpAPI failure must not suppress a valid Google Books cover.

Never expose dozens of Google Image results in the page. Rejected results are diagnostic evidence for tests/logging, not user choices.

## Persistence and precedence

Add truthful provider values without changing existing numeric enum assignments:

```text
ExternalCoverProvider.AmazonViaSerpApi
MediaCoverSource.AmazonViaSerpApi = 7
```

For this source:

- `CoverProviderItemId` is the normalized ASIN.
- `CoverUrl` is the freshly validated Amazon image URL.
- Attribution is the canonical Amazon Japan product URL.
- Library/detail labels should say something understandable such as `Amazon Japan cover (found via Google Images)`.

Add a domain method such as `ApplyAmazonViaSerpApiCover(...)` that independently validates:

- non-empty valid ASIN;
- HTTPS scheme;
- exact image host allowlist;
- maximum URL and item-ID lengths; and
- protected-cover precedence.

The precedence invariant is:

```text
UserOverride/LegacyUnknown > AmazonViaSerpApi > GoogleBooks > OpenLibrary > JitenSpecific > JitenParentFallback > None
```

This precedence applies when deciding whether an automatic source may replace an existing cover. A Jiten relink must not overwrite an Amazon cover; Google Books or dormant Open Library logic must not downgrade it. Manual/user covers remain untouchable.

Update EF configuration, model snapshot, PostgreSQL migration, and the existing local SQLite upgrade path as required. Preserve all old values. Generate the migration with:

```powershell
dotnet tool run dotnet-ef migrations add AddAmazonViaSerpApiCoverSource --project Kiseki.Core --startup-project Kiseki.Core
```

Do not hand-author the generated migration unless a provider-specific adjustment is demonstrably required after generation.

## Review and confirmation security

The browser may submit only the reviewed selection key already bound to server-side batch state. Never accept a posted cover URL, Amazon URL, ASIN, title, score, or dimensions as authority.

At confirmation:

1. Rebuild the same eligible query from trusted server-side TTSU/Jiten context.
2. Repeat the SerpAPI lookup server-side.
3. Find the reviewed ASIN in fresh/cached API results.
4. Re-run every title, exact-volume, qualifier, Amazon-link, image-host, and raster-validation gate.
5. Persist only the freshly validated URL and canonical attribution.

If an explicitly selected manual Amazon option cannot be reproduced, require refreshed review. If an automatically selected optional Amazon cover becomes unavailable, continue importing the valid reading/Jiten metadata without an external cover, matching the current graceful-degradation behavior.

Do not trust the reviewed result merely because SerpAPI returned the same ASIN; the title, volume, branch, source link, and image must still pass.

## Import UX

Keep the import page compact.

- Continue showing the existing per-book/batch lookup progress, but make the wording provider-neutral, for example `Finding volume covers — 5 of 41 books checked`.
- Do not add an Amazon candidate section containing every search result.
- A unique automatic match should show one concise cover preview, dimensions, source, and evidence.
- Genuine ambiguity should use the existing collapsed compact selector and display at most three choices plus `Use no external cover`.
- Each option should show the cover, Amazon Japan label, exact result title, dimensions, and a safe Amazon attribution link. Do not expose technical scoring walls by default.
- Provider failures should be concise: `Amazon cover search unavailable; Google Books fallback checked`, `Amazon cover search not configured`, or `No exact Amazon volume cover found`.
- Missing SerpAPI configuration must not show a scary per-book error; it is an optional provider.
- Preserve radio-button accessibility, labels, keyboard behavior, responsive layout, and the existing explicit no-cover choice.

The UI must never imply that SerpAPI owns the image. Wording should distinguish discovery from source: Amazon Japan is the source; Google Images via SerpAPI found it.

## Tests

Tests must use stubbed `HttpMessageHandler`/fake clients and local byte fixtures. No test may contact SerpAPI, Google, Amazon, Open Library, or Jiten.

Add focused tests for at least:

### Client and configuration

- Missing key performs no SerpAPI request and cleanly falls through.
- Correct endpoint and localization/filter parameters are sent.
- The API key never appears in user-facing errors or logged messages.
- 401/403, 429, 5xx, timeout, malformed JSON, and missing `images_results` map correctly.
- Concurrent identical searches coalesce; canceled/failed calls do not poison later attempts.
- Concurrency never exceeds two.

### Matching

- `Re：ゼロから始める異世界生活 13` accepts the exact Amazon Japan volume 13 result.
- Full-width `１３` and supported `第十三巻`/`第13巻` forms resolve to the same requested volume when supported by existing structured parsing.
- Volume 12 and volume 14 results are rejected for a volume 13 request.
- A manga/comic volume 13 is rejected for a light-novel mainline request.
- An EX, short-story, artbook, omnibus/set, drama-CD, or audiobook result is rejected when qualifiers conflict.
- An Amazon CDN image on a non-Amazon source page is rejected.
- An Amazon product page with a non-allowlisted image host is rejected.
- Invalid/missing ASIN and tracking-only URLs are rejected.
- Duplicate ASIN/image results collapse.
- A unique exact result auto-selects.
- Multiple genuinely different exact results produce no more than three deterministic options.
- Result position alone cannot make a mismatched title/volume pass.
- Actual image validation can reject misleading SerpAPI dimensions.

### Provider order and graceful degradation

- Exact Amazon match prevents unnecessary Google Books/Open Library calls.
- No Amazon match falls through to Google Books.
- SerpAPI unavailable/rate-limited/timed out falls through to Google Books and does not fail import.
- Open Library is not called in the new automatic bulk path.
- Existing Open Library verification/persisted records remain supported.
- An Amazon cover is not overwritten by Google Books, Open Library, or Jiten.
- User/legacy protected covers are not overwritten by Amazon.

### Confirmation and persistence

- Confirmation re-queries/revalidates by trusted context and ASIN.
- A posted/tampered URL cannot be persisted.
- A changed title, volume, branch, source host, ASIN, or image fails confirmation.
- Explicit reviewed cover disappearance requires a refreshed review.
- Automatic optional cover disappearance still imports reading/Jiten metadata without a cover.
- PostgreSQL constraints and local SQLite upgrade accept source 7 with ASIN and reject inconsistent provider IDs.
- Attribution returns the canonical Amazon Japan product URL.

### Web UX

- At most three external cover choices render.
- Selector remains collapsed by default when review is required.
- Unique matches do not produce a massive list.
- Provider-neutral progress shows completed/total books.
- No-key and fallback status copy is accurate.
- Amazon preview uses the validated original URL, lazy loading, and `referrerpolicy="no-referrer"`.

Retain and update all relevant existing Google Books, Open Library, TTSU import, parser, domain, and migration tests. Do not weaken an assertion merely to accommodate the new provider.

## Verification

Run all of the following and resolve failures:

```powershell
dotnet build Kiseki.slnx
dotnet test Kiseki.slnx
dotnet tool run dotnet-ef migrations has-pending-model-changes --project Kiseki.Core --startup-project Kiseki.Core
git diff --check
```

If the optional disposable local PostgreSQL environment is configured, run its integration tests too. Do not point tests or migrations at a remote database.

## Deliverable

Return a concise implementation report containing:

- root cause and architecture chosen;
- files and migrations added/changed;
- exact query and matching rules implemented;
- provider precedence and Open Library compatibility behavior;
- confirmation-time security behavior;
- UX behavior, including the three-option cap;
- configuration instructions for `SerpApi__ApiKey` without revealing a value;
- build/test/migration-check totals; and
- any residual limitation supported by concrete evidence.

Do not claim that the first Google Images result is guaranteed correct. The implementation is complete only when exact title, volume, branch, Amazon product/ASIN, image host, and actual image validation jointly establish the cover, with manual review for the remaining genuine ambiguity.
