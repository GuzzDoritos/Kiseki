using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;

namespace Kiseki.Core.Services.Metadata;

public sealed class JitenMatchService : IJitenMatchService
{
    private readonly IJitenApiClient _jitenApiClient;
    private readonly IJitenSelectionResolver _selectionResolver;
    private readonly IMediaTitleParser _titleParser;
    private readonly IJitenCandidateScorer _candidateScorer;
    private readonly JitenMatchOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayProvider;

    public JitenMatchService(
        IJitenApiClient jitenApiClient,
        IJitenSelectionResolver selectionResolver,
        IMediaTitleParser? titleParser = null,
        IJitenCandidateScorer? candidateScorer = null,
        JitenMatchOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayProvider = null)
    {
        _jitenApiClient = jitenApiClient ?? throw new ArgumentNullException(nameof(jitenApiClient));
        _selectionResolver = selectionResolver ?? throw new ArgumentNullException(nameof(selectionResolver));
        _titleParser = titleParser ?? new MediaTitleParser();
        _candidateScorer = candidateScorer ?? new JitenCandidateScorer(_titleParser);
        _options = options ?? new JitenMatchOptions();

        if (_options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be positive.");
        }

        if (_options.MaxRetries is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must be between zero and two.");
        }

        if (_options.BaseRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseRetryDelay cannot be negative.");
        }

        if (_options.MaxRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryDelay cannot be negative.");
        }

        if (_options.MaxSearchResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSearchResults must be positive.");
        }

        if (_options.MaxDetailBranches <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDetailBranches must be positive.");
        }

        _delayProvider = delayProvider ??
            ((delay, ct) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct));
    }

    public async Task<IReadOnlyList<JitenMatchOutcome>> MatchBatchAsync(
        IReadOnlyList<JitenMatchRequest> requests,
        TimeSpan timeBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (timeBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBudget), "Time budget must be positive.");
        }

        if (requests.Count == 0)
        {
            return [];
        }

        // Validate correlation IDs before any HTTP operations
        var seenIds = new HashSet<Guid>();
        foreach (var req in requests)
        {
            ArgumentNullException.ThrowIfNull(req);

            if (req.CorrelationId == Guid.Empty)
            {
                throw new ArgumentException("CorrelationId cannot be empty.", nameof(requests));
            }

            if (!seenIds.Add(req.CorrelationId))
            {
                throw new ArgumentException($"Duplicate correlation ID '{req.CorrelationId}' detected.", nameof(requests));
            }
        }

        using var budgetCts = new CancellationTokenSource(timeBudget);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetCts.Token);
        var linkedToken = linkedCts.Token;

        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        var startedTasks = new ConcurrentBag<Task>();
        var outcomesByCorrelationId = new ConcurrentDictionary<Guid, JitenMatchOutcome>();

        var searchCache = new ConcurrentDictionary<string, Lazy<Task<SearchResult>>>(StringComparer.OrdinalIgnoreCase);
        var detailCache = new ConcurrentDictionary<int, Lazy<Task<DeckDetailResult>>>();

        // Prepare request contexts with search aliases
        var requestContexts = new List<(JitenMatchRequest Request, ParsedMediaTitle ParsedTitle, IReadOnlyList<string> Aliases)>();

        foreach (var req in requests)
        {
            var parsed = _titleParser.Parse(req.RawTitle);
            if (string.IsNullOrWhiteSpace(parsed.BaseTitle))
            {
                outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                    req.CorrelationId,
                    warnings: ["Search title was empty or unusable."]);
                continue;
            }

            var aliases = parsed.SearchPlan?.SearchAliases is { Count: > 0 } searchAliases
                ? searchAliases
                : [parsed.BaseTitle.Trim().Normalize(NormalizationForm.FormKC)];

            requestContexts.Add((req, parsed, aliases));
        }

        // Progressive multi-alias lookup: try primary alias first, fallback aliases only if needed
        var maxAliases = requestContexts.Count > 0 ? requestContexts.Max(c => c.Aliases.Count) : 0;
        for (var aliasIndex = 0; aliasIndex < maxAliases; aliasIndex++)
        {
            if (linkedToken.IsCancellationRequested)
            {
                break;
            }

            var activeRequests = new List<(JitenMatchRequest Request, ParsedMediaTitle ParsedTitle, string Alias)>();
            foreach (var ctx in requestContexts)
            {
                if (aliasIndex >= ctx.Aliases.Count)
                {
                    continue;
                }

                if (aliasIndex == 0)
                {
                    activeRequests.Add((ctx.Request, ctx.ParsedTitle, ctx.Aliases[0]));
                }
                else
                {
                    // Fallback pass: only try if previous pass produced NoCandidates
                    if (outcomesByCorrelationId.TryGetValue(ctx.Request.CorrelationId, out var existingOutcome) &&
                        existingOutcome.Status == JitenMatchStatus.NoCandidates)
                    {
                        activeRequests.Add((ctx.Request, ctx.ParsedTitle, ctx.Aliases[aliasIndex]));
                    }
                }
            }

            if (activeRequests.Count == 0)
            {
                break;
            }

            var queryGroups = new Dictionary<string, (string Alias, List<(JitenMatchRequest Request, ParsedMediaTitle ParsedTitle)> Requests)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (req, parsed, alias) in activeRequests)
            {
                var queryKey = alias.Trim().Normalize(NormalizationForm.FormKC);
                if (string.IsNullOrWhiteSpace(queryKey))
                {
                    continue;
                }

                if (!queryGroups.TryGetValue(queryKey, out var entry))
                {
                    entry = (alias, []);
                    queryGroups[queryKey] = entry;
                }

                entry.Requests.Add((req, parsed));
            }

            var queryTasks = queryGroups.Select(kvp =>
                ProcessQueryGroupAsync(
                    kvp.Key,
                    kvp.Value.Requests,
                    semaphore,
                    searchCache,
                    detailCache,
                    startedTasks,
                    outcomesByCorrelationId,
                    linkedToken,
                    searchAlias: kvp.Value.Alias,
                    isFallbackPass: aliasIndex > 0)).ToList();

            try
            {
                await Task.WhenAll(queryTasks);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested)
            {
                // Internal time budget expired without caller cancellation
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation: observe all started tasks and rethrow
                await ObserveAllTasksAsync(startedTasks);
                throw;
            }
            catch
            {
                await ObserveAllTasksAsync(startedTasks);
                throw;
            }
        }

        // Ensure all started tasks are completed and observed
        await ObserveAllTasksAsync(startedTasks);

        // Build ordered output matching input requests
        var results = new List<JitenMatchOutcome>(requests.Count);
        foreach (var req in requests)
        {
            if (outcomesByCorrelationId.TryGetValue(req.CorrelationId, out var outcome))
            {
                results.Add(outcome);
            }
            else
            {
                results.Add(JitenMatchOutcome.Unavailable(
                    req.CorrelationId,
                    warnings: ["Request timed out before processing could complete."]));
            }
        }

        return results;
    }

    private async Task ProcessQueryGroupAsync(
        string query,
        List<(JitenMatchRequest Request, ParsedMediaTitle ParsedTitle)> requestsForQuery,
        SemaphoreSlim semaphore,
        ConcurrentDictionary<string, Lazy<Task<SearchResult>>> searchCache,
        ConcurrentDictionary<int, Lazy<Task<DeckDetailResult>>> detailCache,
        ConcurrentBag<Task> startedTasks,
        ConcurrentDictionary<Guid, JitenMatchOutcome> outcomesByCorrelationId,
        CancellationToken cancellationToken,
        string? searchAlias = null,
        bool isFallbackPass = false)
    {
        var searchTask = searchCache.GetOrAdd(
            query,
            q => new Lazy<Task<SearchResult>>(
                () => PerformSearchWithRetryAsync(q, semaphore, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        startedTasks.Add(searchTask);

        SearchResult searchResult;
        try
        {
            searchResult = await searchTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            searchResult = new SearchResult([], IsSuccess: false, IsRateLimited: false, ex.Message);
        }

        if (!searchResult.IsSuccess)
        {
            if (!isFallbackPass)
            {
                foreach (var (req, _) in requestsForQuery)
                {
                    outcomesByCorrelationId[req.CorrelationId] = searchResult.IsRateLimited
                        ? JitenMatchOutcome.RateLimited(req.CorrelationId, warnings: [searchResult.ErrorMessage ?? "Search request was rate limited."])
                        : JitenMatchOutcome.Unavailable(req.CorrelationId, warnings: [searchResult.ErrorMessage ?? "Search request failed."]);
                }
            }
            return;
        }

        if (searchResult.Decks.Count == 0)
        {
            if (!isFallbackPass)
            {
                foreach (var (req, parsed) in requestsForQuery)
                {
                    var emptyResult = _candidateScorer.Score(parsed, []);
                    outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                        req.CorrelationId,
                        emptyResult,
                        warnings: ["No search results returned for title."]);
                }
            }
            return;
        }

        var branchWarnings = new List<string>();
        var candidateList = new List<JitenMatchCandidate>();
        var branchFailures = new List<(int DeckId, bool IsRateLimited, string ErrorMessage)>();

        // Collect distinct target parent deck IDs from search results.
        // Bounded discovery: prefer search rows plausibly related to query while preserving provider order,
        // and cap distinct parent detail requests to MaxDetailBranches.
        var priorityTargetIds = new List<int>();
        var secondaryTargetIds = new List<int>();
        var seenTargetIds = new HashSet<int>();

        foreach (var deck in searchResult.Decks)
        {
            if (deck.ParentDeckId is <= 0)
            {
                branchWarnings.Add($"Ignored search result '{deck.OriginalTitle}' with non-positive parent deck ID {deck.ParentDeckId}.");
                continue;
            }

            var targetId = deck.ParentDeckId ?? deck.DeckId;

            if (targetId <= 0)
            {
                branchWarnings.Add($"Ignored search result '{deck.OriginalTitle}' with non-positive deck ID {targetId}.");
                continue;
            }

            if (seenTargetIds.Add(targetId))
            {
                if (IsPlausiblyRelated(deck, query))
                {
                    priorityTargetIds.Add(targetId);
                }
                else
                {
                    secondaryTargetIds.Add(targetId);
                }
            }
        }

        var candidateTargetIds = priorityTargetIds.Concat(secondaryTargetIds).ToList();
        var totalBranchesDiscovered = candidateTargetIds.Count;
        var targetDeckIds = candidateTargetIds.Take(_options.MaxDetailBranches).ToList();

        if (totalBranchesDiscovered > _options.MaxDetailBranches)
        {
            branchWarnings.Add($"Bounded search inspected {_options.MaxDetailBranches} of {totalBranchesDiscovered} candidate parent branches.");
        }

        foreach (var targetDeckId in targetDeckIds)
        {
            var detailTask = detailCache.GetOrAdd(
                targetDeckId,
                id => new Lazy<Task<DeckDetailResult>>(
                    () => PerformDetailWithRetryAsync(id, semaphore, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            startedTasks.Add(detailTask);

            DeckDetailResult detailResult;
            try
            {
                detailResult = await detailTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                detailResult = new DeckDetailResult(null, IsSuccess: false, IsRateLimited: false, ex.Message);
            }

            if (!detailResult.IsSuccess)
            {
                branchFailures.Add((targetDeckId, detailResult.IsRateLimited, detailResult.ErrorMessage ?? $"Detail request failed for deck {targetDeckId}."));
                branchWarnings.Add($"Failed to fetch detail for deck {targetDeckId}: {detailResult.ErrorMessage}");
                continue;
            }

            if (detailResult.Detail is null)
            {
                branchWarnings.Add($"Deck {targetDeckId} was not found.");
                continue;
            }

            var detail = detailResult.Detail;
            var standaloneResolution = _selectionResolver.Resolve(detail, targetDeckId);
            if (standaloneResolution.IsSuccess && standaloneResolution.Selection is not null)
            {
                candidateList.Add(JitenMatchCandidate.FromSelection(standaloneResolution.Selection));
                continue;
            }

            if (standaloneResolution.Status != JitenSelectionStatus.ParentHasChildren)
            {
                branchWarnings.Add(standaloneResolution.ErrorMessage ?? $"Failed to resolve deck {targetDeckId}.");
                continue;
            }

            // The shared resolver proved this is a parent with children, so only its
            // verified subdecks may become work candidates.
            if (detail.SubDecks.Count == 0)
            {
                branchWarnings.Add($"Parent deck {targetDeckId} indicates subdecks exist but returned no subdeck items.");
                continue;
            }

            foreach (var subdeck in detail.SubDecks)
            {
                if (subdeck.DeckId <= 0)
                {
                    branchWarnings.Add($"Ignored subdeck with non-positive deck ID {subdeck.DeckId} under parent {targetDeckId}.");
                    continue;
                }

                var resolution = _selectionResolver.Resolve(detail, targetDeckId, subdeck.DeckId);
                if (resolution.IsSuccess && resolution.Selection is not null)
                {
                    candidateList.Add(JitenMatchCandidate.FromSelection(resolution.Selection));
                }
                else
                {
                    branchWarnings.Add(resolution.ErrorMessage ?? $"Failed to resolve subdeck {subdeck.DeckId} under parent {targetDeckId}.");
                }
            }
        }

        // De-duplicate candidates by (DeckId, SubdeckId)
        var distinctCandidates = candidateList
            .GroupBy(c => (c.DeckId, c.SubdeckId))
            .Select(g => g.First())
            .ToList();

        if (distinctCandidates.Count > 0)
        {
            // Usable candidates remain (even if some branches failed)
            foreach (var (req, parsed) in requestsForQuery)
            {
                var (filteredCandidates, incompatibleCount, noVolumeWarning, effectiveParsed) =
                    FilterCandidatesForRequest(parsed, distinctCandidates, req.AuthoritativeTtsuTotal);

                var warnings = branchWarnings.ToList();
                if (noVolumeWarning is not null)
                {
                    warnings.Add(noVolumeWarning);
                }

                if (filteredCandidates.Count > 0)
                {
                    if (!string.IsNullOrEmpty(searchAlias) &&
                        !string.Equals(searchAlias, parsed.BaseTitle, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(searchAlias, parsed.SearchPlan?.CanonicalBaseTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"Matched via search alias '{searchAlias}'.");
                    }

                    var scored = _candidateScorer.Score(effectiveParsed, filteredCandidates, req.AuthoritativeTtsuTotal, incompatibleCount);
                    outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.Matched(
                        req.CorrelationId,
                        scored,
                        warnings,
                        matchedAlias: searchAlias);
                }
                else if (!isFallbackPass)
                {
                    var emptyResult = _candidateScorer.Score(effectiveParsed, [], req.AuthoritativeTtsuTotal, incompatibleCount);
                    outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                        req.CorrelationId,
                        emptyResult,
                        warnings);
                }
            }
            return;
        }

        // No usable candidates
        if (branchFailures.Count > 0)
        {
            if (!isFallbackPass)
            {
                // Discovery is incomplete and no successful branch produced a usable candidate.
                var isAllRateLimited = branchFailures.All(f => f.IsRateLimited);
                foreach (var (req, _) in requestsForQuery)
                {
                    outcomesByCorrelationId[req.CorrelationId] = isAllRateLimited
                        ? JitenMatchOutcome.RateLimited(req.CorrelationId, branchWarnings)
                        : JitenMatchOutcome.Unavailable(req.CorrelationId, branchWarnings);
                }
            }
            return;
        }

        // Details succeeded or were partially present, but yielded no usable candidates
        if (!isFallbackPass)
        {
            foreach (var (req, parsed) in requestsForQuery)
            {
                var emptyResult = _candidateScorer.Score(parsed, distinctCandidates, req.AuthoritativeTtsuTotal);
                outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                    req.CorrelationId,
                    emptyResult,
                    branchWarnings);
            }
        }
    }

    private async Task<SearchResult> PerformSearchWithRetryAsync(
        string query,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            var decks = await ExecuteWithRetryAsync(
                ct => _jitenApiClient.SearchBooksBoundedAsync(query, _options.MaxSearchResults, ct),
                semaphore,
                cancellationToken);

            return new SearchResult(decks, IsSuccess: true, IsRateLimited: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var isRateLimited = ex is JitenHttpException { StatusCode: HttpStatusCode.TooManyRequests } ||
                                ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
            return new SearchResult([], IsSuccess: false, IsRateLimited: isRateLimited, ErrorMessage: ex.Message);
        }
    }

    private async Task<DeckDetailResult> PerformDetailWithRetryAsync(
        int deckId,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await ExecuteWithRetryAsync(
                ct => _jitenApiClient.GetDeckDetailAsync(deckId, ct),
                semaphore,
                cancellationToken);

            return new DeckDetailResult(detail, IsSuccess: true, IsRateLimited: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var isRateLimited = ex is JitenHttpException { StatusCode: HttpStatusCode.TooManyRequests } ||
                                ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
            return new DeckDetailResult(null, IsSuccess: false, IsRateLimited: isRateLimited, ErrorMessage: ex.Message);
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var maxRetries = _options.MaxRetries;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries && IsRetryable(ex) && !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
            finally
            {
                semaphore.Release();
            }

            var delay = DetermineRetryDelay(lastException, attempt);
            if (delay > TimeSpan.Zero)
            {
                await _delayProvider(delay, cancellationToken);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("Retry loop completed without result or exception.");
    }

    private TimeSpan DetermineRetryDelay(Exception? ex, int attempt)
    {
        if (ex is JitenHttpException { RetryAfter: not null } jex && jex.RetryAfter.Value > TimeSpan.Zero)
        {
            return jex.RetryAfter.Value;
        }

        if (_options.BaseRetryDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var factor = Math.Pow(2, attempt);
        var delayMs = _options.BaseRetryDelay.TotalMilliseconds * factor;
        var jitterMs = Random.Shared.Next(0, 50);
        var totalDelay = TimeSpan.FromMilliseconds(delayMs + jitterMs);

        return totalDelay > _options.MaxRetryDelay ? _options.MaxRetryDelay : totalDelay;
    }

    private static bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }

        if (ex is JitenHttpException jex && jex.StatusCode.HasValue)
        {
            return jex.StatusCode.Value == HttpStatusCode.TooManyRequests ||
                   ((int)jex.StatusCode.Value >= 500 && (int)jex.StatusCode.Value <= 599);
        }

        if (ex is HttpRequestException generalHttp)
        {
            if (generalHttp.StatusCode.HasValue)
            {
                return generalHttp.StatusCode.Value == HttpStatusCode.TooManyRequests ||
                       ((int)generalHttp.StatusCode.Value >= 500 && (int)generalHttp.StatusCode.Value <= 599);
            }

            return true;
        }

        return false;
    }

    private static async Task ObserveAllTasksAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks.Distinct())
        {
            try
            {
                await task;
            }
            catch
            {
                // Suppressed after observing
            }
        }
    }

    private static bool IsPlausiblyRelated(JitenDeckDTO deck, string requestedBaseTitle)
    {
        if (string.IsNullOrWhiteSpace(requestedBaseTitle))
        {
            return false;
        }

        var titles = new[] { deck.OriginalTitle, deck.EnglishTitle, deck.RomajiTitle };
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var parsed = MediaTitleParser.ParseTitle(title);
            var candidateBase = string.IsNullOrWhiteSpace(parsed.BaseTitle) ? title.Trim() : parsed.BaseTitle;

            if (string.Equals(requestedBaseTitle, candidateBase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var minLength = Math.Min(requestedBaseTitle.Length, candidateBase.Length);
            var maxLength = Math.Max(requestedBaseTitle.Length, candidateBase.Length);

            if (minLength >= 4 && (double)minLength / maxLength >= 0.5)
            {
                if (requestedBaseTitle.Contains(candidateBase, StringComparison.OrdinalIgnoreCase) ||
                    candidateBase.Contains(requestedBaseTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private (List<JitenMatchCandidate> Candidates, int IncompatibleCount, string? Warning, ParsedMediaTitle EffectiveParsed) FilterCandidatesForRequest(
        ParsedMediaTitle parsedTitle,
        IReadOnlyList<JitenMatchCandidate> distinctCandidates,
        int? authoritativeTtsuTotal)
    {
        var filtered = new List<JitenMatchCandidate>();
        var incompatibleCount = 0;
        string? warning = null;
        var effectiveParsed = parsedTitle;

        var requestedQualifier = parsedTitle.SearchPlan?.SeriesQualifier ?? MediaTitleParser.ExtractSeriesQualifier(parsedTitle.OriginalTitle);

        // Step 1: Pre-filter distinct candidates by series qualifier compatibility.
        // Known-incompatible branches must be eliminated early so they don't produce false ties.
        var qualifierCompatible = new List<JitenMatchCandidate>();
        foreach (var candidate in distinctCandidates)
        {
            var candidateQualifier = MediaTitleParser.GetCandidateSeriesQualifier(candidate);
            if (requestedQualifier != candidateQualifier)
            {
                if (requestedQualifier != SeriesQualifier.Mainline || candidateQualifier != SeriesQualifier.Mainline)
                {
                    incompatibleCount++;
                    continue;
                }
            }
            qualifierCompatible.Add(candidate);
        }

        var requestedVolume = parsedTitle.Volume;

        // Case A: Explicit Volume parsed on the title
        if (requestedVolume is not null)
        {
            foreach (var candidate in qualifierCompatible)
            {
                if (candidate.IsStandalone)
                {
                    filtered.Add(candidate);
                    continue;
                }

                if (!candidate.IsSubdeck)
                {
                    continue;
                }

                var extraction = MediaTitleParser.ExtractCandidateVolumes(candidate, _titleParser);

                if (extraction.HasInternalConflict)
                {
                    var anyMatches = extraction.VariantVolumes.Any(v => requestedVolume.Matches(v.Volume));
                    if (anyMatches)
                    {
                        filtered.Add(candidate);
                    }
                    else
                    {
                        incompatibleCount++;
                    }
                    continue;
                }

                if (extraction.PrimaryVolume is null)
                {
                    incompatibleCount++;
                    continue;
                }

                if (requestedVolume.Matches(extraction.PrimaryVolume))
                {
                    filtered.Add(candidate);
                }
                else
                {
                    incompatibleCount++;
                }
            }

            return (filtered, incompatibleCount, warning, effectiveParsed);
        }

        // Case B: Tentative Attached ASCII Volume Hypothesis (e.g. Ｒｅ：ゼロから始める異世界生活5)
        if (parsedTitle.SearchPlan?.VolumeInference == VolumeInferenceKind.AttachedAsciiHypothesis &&
            parsedTitle.SearchPlan?.Volume is not null)
        {
            var tentativeVolume = parsedTitle.SearchPlan.Volume;
            var canonicalBase = parsedTitle.SearchPlan.CanonicalBaseTitle ?? parsedTitle.BaseTitle;
            var normCanonical = NormalizeForComparison(canonicalBase);

            // Provider confirmation requirements:
            // 1. tentative base matches a Jiten parent title exactly after established normalization
            // 2. parent has a uniquely identified child with that exact standard volume
            // 3. no conflicting explicit marker
            // 4. candidate compatible with requested series qualifier
            var parentMatches = qualifierCompatible
                .Where(c => c.IsSubdeck && IsParentExactMatch(c, normCanonical))
                .ToList();

            if (parentMatches.Count > 0)
            {
                var parentGroups = parentMatches.GroupBy(c => c.DeckId).ToList();
                var confirmedCandidates = new List<JitenMatchCandidate>();

                foreach (var pGroup in parentGroups)
                {
                    var matchingChildren = pGroup
                        .Where(c =>
                        {
                            var ext = MediaTitleParser.ExtractCandidateVolumes(c, _titleParser);
                            return ext.PrimaryVolume is not null && tentativeVolume.Matches(ext.PrimaryVolume);
                        })
                        .ToList();

                    if (matchingChildren.Count == 1)
                    {
                        confirmedCandidates.Add(matchingChildren[0]);
                    }
                }

                if (confirmedCandidates.Count == 1)
                {
                    var confirmed = confirmedCandidates[0];
                    filtered.Add(confirmed);

                    incompatibleCount += qualifierCompatible.Count - 1;
                    return (filtered, incompatibleCount, null, effectiveParsed);
                }
            }

            incompatibleCount += qualifierCompatible.Count;
            warning = $"Attached volume hypothesis '{tentativeVolume.Number}' was not confirmed by provider series metadata.";
            return (filtered, incompatibleCount, warning, effectiveParsed);
        }

        // Case C: Unnumbered title (check for Provider-Confirmed Implicit Volume 1)
        // Conditions for Implicit Volume 1:
        // 1. Cleaned title exactly matches a Jiten parent title
        // 2. Parent is a multi-volume deck
        // 3. Exactly one verified child represents standard Volume 1
        // 4. No standalone exact-title deck is a better candidate
        // 5. No special-series qualifier conflicts (already checked by qualifierCompatible)
        // 6. Authoritative TTSU total, when available, within safe 30% tolerance
        // 7. No competing parent produces equally strong evidence

        var cleanedTitle = parsedTitle.SearchPlan?.CanonicalBaseTitle ?? parsedTitle.BaseTitle;
        var normCleaned = NormalizeForComparison(cleanedTitle);

        var exactStandalone = qualifierCompatible.FirstOrDefault(c =>
            c.IsStandalone &&
            (string.Equals(NormalizeForComparison(c.OriginalTitle), normCleaned, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeForComparison(c.EnglishTitle), normCleaned, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeForComparison(c.RomajiTitle), normCleaned, StringComparison.OrdinalIgnoreCase)));

        if (exactStandalone is not null)
        {
            filtered.Add(exactStandalone);
            foreach (var c in qualifierCompatible)
            {
                if (c != exactStandalone)
                {
                    incompatibleCount++;
                }
            }
            return (filtered, incompatibleCount, null, effectiveParsed);
        }

        var parentSubdecks = qualifierCompatible
            .Where(c => c.IsSubdeck && IsParentExactMatch(c, normCleaned))
            .ToList();

        if (parentSubdecks.Count > 0)
        {
            var parentGroups = parentSubdecks.GroupBy(c => c.DeckId).ToList();

            if (parentGroups.Count == 1)
            {
                var pGroup = parentGroups[0];
                var allChildrenOfParent = qualifierCompatible.Where(c => c.DeckId == pGroup.Key).ToList();
                if (allChildrenOfParent.Count >= 2 || (allChildrenOfParent.FirstOrDefault()?.ChildrenDeckCount ?? 0) >= 2)
                {
                    var vol1Candidates = allChildrenOfParent
                        .Where(c =>
                        {
                            var ext = MediaTitleParser.ExtractCandidateVolumes(c, _titleParser);
                            return ext.PrimaryVolume?.Kind == VolumeKind.Standard && ext.PrimaryVolume?.Number == 1;
                        })
                        .ToList();

                    if (vol1Candidates.Count == 1)
                    {
                        var vol1Candidate = vol1Candidates[0];

                        bool charCountSafe = true;
                        if (authoritativeTtsuTotal.HasValue && authoritativeTtsuTotal.Value > 0)
                        {
                            if (vol1Candidate.CharacterCount <= 0)
                            {
                                charCountSafe = false;
                            }
                            else
                            {
                                var diff = Math.Abs((double)authoritativeTtsuTotal.Value - vol1Candidate.CharacterCount) /
                                           Math.Max((double)authoritativeTtsuTotal.Value, vol1Candidate.CharacterCount);
                                if (diff > 0.30)
                                {
                                    charCountSafe = false;
                                }
                            }
                        }

                        if (charCountSafe)
                        {
                            filtered.Add(vol1Candidate);
                            incompatibleCount += qualifierCompatible.Count - 1;

                            var effectivePlan = (parsedTitle.SearchPlan ?? new TitleSearchPlan
                            {
                                OriginalTitle = parsedTitle.OriginalTitle,
                                ComparisonTitle = parsedTitle.ComparisonTitle,
                                CanonicalBaseTitle = parsedTitle.BaseTitle
                            }) with
                            {
                                Volume = StructuredVolume.Standard(1),
                                VolumeInference = VolumeInferenceKind.ImplicitFirstVolume
                            };

                            effectiveParsed = new ParsedMediaTitle
                            {
                                OriginalTitle = parsedTitle.OriginalTitle,
                                ComparisonTitle = parsedTitle.ComparisonTitle,
                                BaseTitle = parsedTitle.BaseTitle,
                                Volume = null,
                                ParsingNotes = [.. parsedTitle.ParsingNotes, "Provider-confirmed implicit Volume 1"],
                                SearchPlan = effectivePlan
                            };

                            return (filtered, incompatibleCount, null, effectiveParsed);
                        }
                    }
                }
            }
        }

        var subdeckCount = 0;
        foreach (var candidate in qualifierCompatible)
        {
            if (candidate.IsStandalone)
            {
                filtered.Add(candidate);
            }
            else if (candidate.IsSubdeck)
            {
                subdeckCount++;
                incompatibleCount++;
            }
        }

        if (subdeckCount > 0 && filtered.Count == 0)
        {
            warning = "No volume number could be identified; child volumes were not suggested automatically.";
        }

        return (filtered, incompatibleCount, warning, effectiveParsed);
    }

    private static bool IsParentExactMatch(JitenMatchCandidate candidate, string normalizedTarget)
    {
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ParentOriginalTitle) &&
            string.Equals(NormalizeForComparison(candidate.ParentOriginalTitle), normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ParentEnglishTitle) &&
            string.Equals(NormalizeForComparison(candidate.ParentEnglishTitle), normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ParentRomajiTitle) &&
            string.Equals(NormalizeForComparison(candidate.ParentRomajiTitle), normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeForComparison(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        return title.Normalize(NormalizationForm.FormKC).Trim();
    }

    private sealed record SearchResult(
        IReadOnlyList<JitenDeckDTO> Decks,
        bool IsSuccess,
        bool IsRateLimited,
        string? ErrorMessage);

    private sealed record DeckDetailResult(
        JitenDeckDetailDTO? Detail,
        bool IsSuccess,
        bool IsRateLimited,
        string? ErrorMessage);
}
