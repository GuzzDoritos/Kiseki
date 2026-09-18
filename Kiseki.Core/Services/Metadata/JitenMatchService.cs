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

        // Parse titles and separate requests with unusable queries
        var queryGroups = new Dictionary<string, List<(JitenMatchRequest Request, ParsedMediaTitle ParsedTitle)>>(StringComparer.OrdinalIgnoreCase);

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

            var queryKey = parsed.BaseTitle.Trim().Normalize(NormalizationForm.FormKC);
            if (!queryGroups.TryGetValue(queryKey, out var list))
            {
                list = [];
                queryGroups[queryKey] = list;
            }

            list.Add((req, parsed));
        }

        // Launch processing for distinct query groups
        var queryTasks = queryGroups.Select(kvp =>
            ProcessQueryGroupAsync(
                kvp.Key,
                kvp.Value,
                semaphore,
                searchCache,
                detailCache,
                startedTasks,
                outcomesByCorrelationId,
                linkedToken)).ToList();

        try
        {
            await Task.WhenAll(queryTasks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            // Internal time budget expired without caller cancellation
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
                    warnings: ["Operation timed out before match could complete."]));
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
        CancellationToken cancellationToken)
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
            foreach (var (req, _) in requestsForQuery)
            {
                outcomesByCorrelationId[req.CorrelationId] = searchResult.IsRateLimited
                    ? JitenMatchOutcome.RateLimited(req.CorrelationId, warnings: [searchResult.ErrorMessage ?? "Search request was rate limited."])
                    : JitenMatchOutcome.Unavailable(req.CorrelationId, warnings: [searchResult.ErrorMessage ?? "Search request failed."]);
            }
            return;
        }

        if (searchResult.Decks.Count == 0)
        {
            foreach (var (req, parsed) in requestsForQuery)
            {
                var emptyResult = _candidateScorer.Score(parsed, []);
                outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                    req.CorrelationId,
                    emptyResult,
                    warnings: ["No search results returned for title."]);
            }
            return;
        }

        var branchWarnings = new List<string>();
        var candidateList = new List<JitenMatchCandidate>();
        var branchFailures = new List<(int DeckId, bool IsRateLimited, string ErrorMessage)>();

        // Collect distinct target parent deck IDs from search results
        var targetDeckIds = new List<int>();
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
                targetDeckIds.Add(targetId);
            }
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
                var scored = _candidateScorer.Score(parsed, distinctCandidates, req.AuthoritativeTtsuTotal);
                outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.Matched(
                    req.CorrelationId,
                    scored,
                    branchWarnings);
            }
            return;
        }

        // No usable candidates
        if (branchFailures.Count > 0)
        {
            // Discovery is incomplete and no successful branch produced a usable candidate.
            var isAllRateLimited = branchFailures.All(f => f.IsRateLimited);
            foreach (var (req, _) in requestsForQuery)
            {
                outcomesByCorrelationId[req.CorrelationId] = isAllRateLimited
                    ? JitenMatchOutcome.RateLimited(req.CorrelationId, branchWarnings)
                    : JitenMatchOutcome.Unavailable(req.CorrelationId, branchWarnings);
            }
            return;
        }

        // Details succeeded or were partially present, but yielded no usable candidates
        foreach (var (req, parsed) in requestsForQuery)
        {
            var emptyResult = _candidateScorer.Score(parsed, distinctCandidates, req.AuthoritativeTtsuTotal);
            outcomesByCorrelationId[req.CorrelationId] = JitenMatchOutcome.NoCandidates(
                req.CorrelationId,
                emptyResult,
                branchWarnings);
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
                ct => _jitenApiClient.SearchBooksAsync(query, ct),
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
