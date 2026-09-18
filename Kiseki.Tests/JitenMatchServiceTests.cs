using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class JitenMatchServiceTests
{
    private static JitenMatchOptions FastOptions(int maxConcurrency = 3) =>
        new()
        {
            MaxConcurrency = maxConcurrency,
            MaxRetries = 2,
            BaseRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero
        };

    [Fact]
    public async Task MatchBatchAsync_EmptyBatch_MakesNoHttpCalls()
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync([], TimeSpan.FromSeconds(5));

        Assert.Empty(outcomes);
        Assert.Equal(0, client.SearchCallCount);
        Assert.Equal(0, client.DetailCallCount);
    }

    [Fact]
    public async Task MatchBatchAsync_GuidEmpty_FailsBeforeHttp()
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = Guid.Empty, RawTitle = "Title 1" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5)));

        Assert.Equal(0, client.SearchCallCount);
        Assert.Equal(0, client.DetailCallCount);
    }

    [Fact]
    public async Task MatchBatchAsync_DuplicateCorrelationIds_FailsBeforeHttp()
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var sharedId = Guid.NewGuid();
        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = sharedId, RawTitle = "Title 1" },
            new JitenMatchRequest { CorrelationId = sharedId, RawTitle = "Title 2" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5)));

        Assert.Equal(0, client.SearchCallCount);
        Assert.Equal(0, client.DetailCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MatchBatchAsync_NonPositiveBudget_FailsBeforeHttp(int seconds)
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.MatchBatchAsync(requests, TimeSpan.FromSeconds(seconds)));

        Assert.Equal(0, client.SearchCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveConcurrency_IsRejected(int maxConcurrency)
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JitenMatchService(
                client,
                resolver,
                options: FastOptions(maxConcurrency)));

        Assert.Equal(0, client.SearchCallCount);
        Assert.Equal(0, client.DetailCallCount);
    }

    [Fact]
    public void Constructor_MoreThanTwoRetries_IsRejected()
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JitenMatchService(
                client,
                resolver,
                options: FastOptions() with { MaxRetries = 3 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".epub")]
    public async Task MatchBatchAsync_EmptyOrUnusableParsedQuery_ReturnsNoCandidatesWithoutHttp(string rawTitle)
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id = Guid.NewGuid();
        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = id, RawTitle = rawTitle }
        };

        var outcomes = await service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(id, outcome.CorrelationId);
        Assert.Equal(JitenMatchStatus.NoCandidates, outcome.Status);
        Assert.Equal(0, client.SearchCallCount);
    }

    [Fact]
    public async Task MatchBatchAsync_OneSearchPerDistinctNormalizedBaseTitle()
    {
        var client = new MockJitenApiClient();
        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Sword Art Online 1" },
            new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Sword Art Online 2" },
            new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Overlord Vol. 1" }
        };

        var outcomes = await service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5));

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(2, client.SearchCallCount);
        Assert.Contains("Sword Art Online", client.SearchQueries);
        Assert.Contains("Overlord", client.SearchQueries);
    }

    [Fact]
    public async Task MatchBatchAsync_TwoVolumesSharingBaseQuery_ReuseSearchAndParentDetail()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = query => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10, OriginalTitle = "Sword Art Online", ChildrenDeckCount = 2 }
            ]),
            DetailHandler = deckId => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, OriginalTitle = "Sword Art Online", ChildrenDeckCount = 2 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Sword Art Online 1", CharacterCount = 100_000 },
                    new JitenDeckDTO { DeckId = 12, OriginalTitle = "Sword Art Online 2", CharacterCount = 110_000 }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = id1, RawTitle = "Sword Art Online 1", AuthoritativeTtsuTotal = 100_000 },
            new JitenMatchRequest { CorrelationId = id2, RawTitle = "Sword Art Online 2", AuthoritativeTtsuTotal = 110_000 }
        };

        var outcomes = await service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5));

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(1, client.SearchCallCount);
        Assert.Equal(1, client.DetailCallCount);

        var o1 = outcomes.Single(o => o.CorrelationId == id1);
        Assert.Equal(JitenMatchStatus.Matched, o1.Status);
        Assert.Equal(11, o1.Result?.BestCandidate?.Candidate.SubdeckId);
        Assert.Equal(MatchConfidence.High, o1.Result?.Confidence);

        var o2 = outcomes.Single(o => o.CorrelationId == id2);
        Assert.Equal(JitenMatchStatus.Matched, o2.Status);
        Assert.Equal(12, o2.Result?.BestCandidate?.Candidate.SubdeckId);
        Assert.Equal(MatchConfidence.High, o2.Result?.Confidence);
    }

    [Fact]
    public async Task MatchBatchAsync_DistinctQueriesSharingParent_UseOneDetailCall()
    {
        var searchesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchCount = 0;
        var client = new MockJitenApiClient
        {
            SearchHandlerWithToken = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref searchCount) == 2)
                {
                    searchesReady.TrySetResult();
                }

                await searchesReady.Task.WaitAsync(cancellationToken);
                return [new JitenDeckDTO { DeckId = 11, ParentDeckId = 10 }];
            },
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 1 },
                SubDecks = [new JitenDeckDTO { DeckId = 11, OriginalTitle = "Shared Series 1" }]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [
                new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Localized Name 1" },
                new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Shared Series 1" }
            ],
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(2, client.SearchCallCount);
        Assert.Equal(1, client.DetailCallCount);
    }

    [Fact]
    public async Task MatchBatchAsync_SearchResultsWithParentDeckId_LoadsAndVerifiesParentDetail()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 55, ParentDeckId = 50, OriginalTitle = "Child Vol 1" }
            ]),
            DetailHandler = deckId => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 50, OriginalTitle = "Parent Series", ChildrenDeckCount = 1 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 55, OriginalTitle = "Child Vol 1", CharacterCount = 50_000 }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id = Guid.NewGuid();
        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = id, RawTitle = "Child Vol 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.Equal(50, client.DetailDeckIds.Single());
    }

    [Fact]
    public async Task MatchBatchAsync_DuplicateParentAndChildSearchRows_ReuseDetailAndCandidates()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10, ParentDeckId = null, OriginalTitle = "Series" },
                new JitenDeckDTO { DeckId = 11, ParentDeckId = 10, OriginalTitle = "Volume 1" },
                new JitenDeckDTO { DeckId = 12, ParentDeckId = 10, OriginalTitle = "Volume 2" }
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, OriginalTitle = "Series", ChildrenDeckCount = 2 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Volume 1" },
                    new JitenDeckDTO { DeckId = 12, OriginalTitle = "Volume 2" }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id = Guid.NewGuid();
        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = id, RawTitle = "Volume 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.Equal(1, client.DetailCallCount);
        Assert.Equal(2, outcome.Result?.Candidates.Count);
    }

    [Fact]
    public async Task MatchBatchAsync_FreshStandaloneVerification_AcceptsStandaloneDeck()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 42, OriginalTitle = "One Shot Story", ChildrenDeckCount = 0 }
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 42, OriginalTitle = "One Shot Story", ChildrenDeckCount = 0 },
                SubDecks = []
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id = Guid.NewGuid();
        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = id, RawTitle = "One Shot Story" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        var candidate = outcome.Result?.BestCandidate?.Candidate;
        Assert.NotNull(candidate);
        Assert.Equal(42, candidate.DeckId);
        Assert.Null(candidate.SubdeckId);
        Assert.True(candidate.IsStandalone);
    }

    [Fact]
    public async Task MatchBatchAsync_ParentCandidateExcludedWhenChildrenExist_ExpandsSubdecksOnly()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10, OriginalTitle = "Series", ChildrenDeckCount = 0 } // Search hint says 0
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                // Fresh detail reveals 2 children!
                MainDeck = new JitenDeckDTO { DeckId = 10, OriginalTitle = "Series", ChildrenDeckCount = 2 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Series Vol 1" },
                    new JitenDeckDTO { DeckId = 12, OriginalTitle = "Series Vol 2" }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Series Vol 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.All(outcome.Result.Candidates, c => Assert.True(c.Candidate.IsSubdeck));
        Assert.DoesNotContain(outcome.Result.Candidates, c => c.Candidate.DeckId == 10 && !c.Candidate.SubdeckId.HasValue);
    }

    [Fact]
    public async Task MatchBatchAsync_CandidateDeduplicationByDeckAndSubdeck()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10 },
                new JitenDeckDTO { DeckId = 10 }
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 1 },
                SubDecks = [new JitenDeckDTO { DeckId = 11, OriginalTitle = "Vol 1" }]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Vol 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.Single(outcome.Result!.Candidates);
    }

    [Fact]
    public async Task MatchBatchAsync_SafeCoverUrlAndCoverEvidenceSurviveConversion()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10 }
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    CoverName = "https://cdn.jiten.moe/parent.jpg",
                    ChildrenDeckCount = 3
                },
                SubDecks =
                [
                    new JitenDeckDTO
                    {
                        DeckId = 11,
                        OriginalTitle = "Vol 1",
                        CoverName = "https://cdn.jiten.moe/v1.jpg"
                    },
                    new JitenDeckDTO
                    {
                        DeckId = 12,
                        OriginalTitle = "Vol 2",
                        CoverName = "" // Falls back to parent cover
                    },
                    new JitenDeckDTO
                    {
                        DeckId = 13,
                        OriginalTitle = "Vol 3",
                        CoverName = "nocover.jpg" // Rejected cover name
                    }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Vol 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        var candidates = outcome.Result!.Candidates.Select(c => c.Candidate).ToList();

        var c1 = candidates.Single(c => c.SubdeckId == 11);
        Assert.Equal("https://cdn.jiten.moe/v1.jpg", c1.CoverUrl);
        Assert.Equal(JitenCoverEvidence.Specific, c1.CoverEvidence);

        var c2 = candidates.Single(c => c.SubdeckId == 12);
        Assert.Equal("https://cdn.jiten.moe/parent.jpg", c2.CoverUrl);
        Assert.Equal(JitenCoverEvidence.ParentFallback, c2.CoverEvidence);

        // Deck 13 falls back to parent cover since nocover.jpg was normalized to null
        var c3 = candidates.Single(c => c.SubdeckId == 13);
        Assert.Equal("https://cdn.jiten.moe/parent.jpg", c3.CoverUrl);
        Assert.Equal(JitenCoverEvidence.ParentFallback, c3.CoverEvidence);
    }

    [Fact]
    public async Task MatchBatchAsync_InvalidNonPositiveJitenIds_SkippedWithWarnings()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = -1, OriginalTitle = "Invalid 1" },
                new JitenDeckDTO { DeckId = 12, ParentDeckId = -10, OriginalTitle = "Invalid parent" },
                new JitenDeckDTO { DeckId = 10, OriginalTitle = "Valid Series" }
            ]),
            DetailHandler = deckId => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 2 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = -99, OriginalTitle = "Negative subdeck" },
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Valid Vol 1" }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Valid Series 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Single(outcome.Result!.Candidates);
        Assert.Equal(11, outcome.Result.Candidates[0].Candidate.SubdeckId);
        Assert.Equal([10], client.DetailDeckIds);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("non-positive parent deck ID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchBatchAsync_SeparateScoringPerRequestTitleAndTotal()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10, OriginalTitle = "Series" }
            ]),
            DetailHandler = _ => Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 2 },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Series 1", CharacterCount = 100_000 },
                    new JitenDeckDTO { DeckId = 12, OriginalTitle = "Series 2", CharacterCount = 200_000 }
                ]
            })
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var outcomes = await service.MatchBatchAsync(
            [
                new JitenMatchRequest { CorrelationId = id1, RawTitle = "Series 1", AuthoritativeTtsuTotal = 100_000 },
                new JitenMatchRequest { CorrelationId = id2, RawTitle = "Series 2", AuthoritativeTtsuTotal = 200_000 }
            ],
            TimeSpan.FromSeconds(5));

        var o1 = outcomes.Single(o => o.CorrelationId == id1);
        var o2 = outcomes.Single(o => o.CorrelationId == id2);

        Assert.Equal(11, o1.Result!.BestCandidate!.Candidate.SubdeckId);
        Assert.Equal(12, o2.Result!.BestCandidate!.Candidate.SubdeckId);
    }

    [Fact]
    public async Task MatchBatchAsync_Ordinary4xx_NotRetried()
    {
        var attempts = 0;
        var client = new MockJitenApiClient
        {
            SearchHandler = _ =>
            {
                attempts++;
                throw new JitenHttpException("Bad request", HttpStatusCode.BadRequest);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(1, attempts);
        Assert.Equal(JitenMatchStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task MatchBatchAsync_MalformedJson_NotRetried()
    {
        var attempts = 0;
        var client = new MockJitenApiClient
        {
            SearchHandler = _ =>
            {
                attempts++;
                throw new JsonException("Corrupted JSON");
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(1, attempts);
        Assert.Equal(JitenMatchStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task MatchBatchAsync_CallerCancellation_NotRetriedAndPropagates()
    {
        var attempts = 0;
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MockJitenApiClient
        {
            SearchHandlerWithToken = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref attempts);
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        using var cts = new CancellationTokenSource();
        var matchTask = service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5),
            cts.Token);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => matchTask);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task MatchBatchAsync_OneFailedDetailBranchDoesNotEraseCandidateFromSuccessfulBranch_ReturnsMatchedWithWarnings()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10 },
                new JitenDeckDTO { DeckId = 20 }
            ]),
            DetailHandler = deckId =>
            {
                if (deckId == 10)
                {
                    return Task.FromResult<JitenDeckDetailDTO?>(new JitenDeckDetailDTO
                    {
                        MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 1 },
                        SubDecks = [new JitenDeckDTO { DeckId = 11, OriginalTitle = "Working Candidate 1" }]
                    });
                }

                throw new JitenHttpException("Deck 20 failed", HttpStatusCode.InternalServerError);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Working Candidate 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(JitenMatchStatus.Matched, outcome.Status);
        Assert.NotEmpty(outcome.Warnings);
        Assert.Single(outcome.Result!.Candidates);
        Assert.Equal(11, outcome.Result.BestCandidate!.Candidate.SubdeckId);
    }

    [Fact]
    public async Task MatchBatchAsync_Exhausted429_ReturnsRateLimited()
    {
        var attempts = 0;
        var client = new MockJitenApiClient
        {
            SearchHandler = _ =>
            {
                attempts++;
                throw new JitenHttpException("Too many requests", HttpStatusCode.TooManyRequests);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(3, attempts); // 1 initial + 2 retries = 3
        Assert.Equal(JitenMatchStatus.RateLimited, outcome.Status);
    }

    [Fact]
    public async Task MatchBatchAsync_Exhausted5xx_ReturnsUnavailable()
    {
        var attempts = 0;
        var client = new MockJitenApiClient
        {
            SearchHandler = _ =>
            {
                attempts++;
                throw new JitenHttpException("Internal server error", HttpStatusCode.InternalServerError);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(3, attempts); // 1 initial + 2 retries = 3
        Assert.Equal(JitenMatchStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task MatchBatchAsync_429RetryWithRetryAfterSucceeds()
    {
        var attempts = 0;
        TimeSpan? observedDelay = null;
        var retryAfter = TimeSpan.FromMinutes(2);
        var client = new MockJitenApiClient
        {
            SearchHandler = _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new JitenHttpException("Rate limited", HttpStatusCode.TooManyRequests, retryAfter);
                }

                return Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(
            client,
            resolver,
            options: FastOptions(),
            delayProvider: (delay, _) =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            });

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(2, attempts);
        Assert.Equal(retryAfter, observedDelay);
        Assert.Equal(JitenMatchStatus.NoCandidates, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, JitenMatchStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, JitenMatchStatus.Unavailable)]
    public async Task MatchBatchAsync_FailedBranchWithOtherEmptyBranch_DoesNotReportNoCandidates(
        HttpStatusCode failureStatus,
        JitenMatchStatus expectedStatus)
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = _ => Task.FromResult<IReadOnlyList<JitenDeckDTO>>(
            [
                new JitenDeckDTO { DeckId = 10 },
                new JitenDeckDTO { DeckId = 20 }
            ]),
            DetailHandler = deckId => deckId == 10
                ? Task.FromResult<JitenDeckDetailDTO?>(null)
                : throw new JitenHttpException("Discovery branch failed", failureStatus)
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = Guid.NewGuid(), RawTitle = "Title 1" }],
            TimeSpan.FromSeconds(5));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(expectedStatus, outcome.Status);
        Assert.NotEmpty(outcome.Warnings);
    }

    [Fact]
    public async Task MatchBatchAsync_InternalBudgetExpiry_ReturnsUnavailableWithoutLeakedWork()
    {
        var requestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new MockJitenApiClient
        {
            SearchHandlerWithToken = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return [];
                }
                finally
                {
                    requestCompleted.TrySetResult();
                }
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id = Guid.NewGuid();
        var outcomes = await service.MatchBatchAsync(
            [new JitenMatchRequest { CorrelationId = id, RawTitle = "Title 1" }],
            TimeSpan.FromMilliseconds(50));

        var outcome = Assert.Single(outcomes);
        Assert.Equal(id, outcome.CorrelationId);
        Assert.Equal(JitenMatchStatus.Unavailable, outcome.Status);
        await requestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MatchBatchAsync_OutputOrderMatchesInputOrder()
    {
        var client = new MockJitenApiClient
        {
            SearchHandler = query =>
            {
                if (query.Contains("Fast")) return Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);
                return Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions());

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var id4 = Guid.NewGuid();

        var requests = new[]
        {
            new JitenMatchRequest { CorrelationId = id1, RawTitle = "Title 1" },
            new JitenMatchRequest { CorrelationId = id2, RawTitle = "" },
            new JitenMatchRequest { CorrelationId = id3, RawTitle = "Fast 3" },
            new JitenMatchRequest { CorrelationId = id4, RawTitle = "Title 4" }
        };

        var outcomes = await service.MatchBatchAsync(requests, TimeSpan.FromSeconds(5));

        Assert.Equal(4, outcomes.Count);
        Assert.Equal(id1, outcomes[0].CorrelationId);
        Assert.Equal(id2, outcomes[1].CorrelationId);
        Assert.Equal(id3, outcomes[2].CorrelationId);
        Assert.Equal(id4, outcomes[3].CorrelationId);
    }

    [Fact]
    public async Task MatchBatchAsync_100BookSyntheticBatch_NeverExceedsConfiguredConcurrency_AndUsesNoRealDelays()
    {
        const int maxConcurrency = 3;
        var client = new MockJitenApiClient
        {
            SearchHandler = async query =>
            {
                await Task.Yield();
                return [new JitenDeckDTO { DeckId = 100, OriginalTitle = query, ChildrenDeckCount = 0 }];
            },
            DetailHandler = async deckId =>
            {
                await Task.Yield();
                return new JitenDeckDetailDTO
                {
                    MainDeck = new JitenDeckDTO { DeckId = deckId, OriginalTitle = "Book", ChildrenDeckCount = 0 },
                    SubDecks = []
                };
            }
        };

        var resolver = new JitenSelectionResolver(client);
        var service = new JitenMatchService(client, resolver, options: FastOptions(maxConcurrency));

        var requests = Enumerable.Range(1, 100)
            .Select(i => new JitenMatchRequest
            {
                CorrelationId = Guid.NewGuid(),
                RawTitle = $"Synthetic Book {i}"
            })
            .ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcomes = await service.MatchBatchAsync(requests, TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Assert.Equal(100, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(JitenMatchStatus.Matched, o.Status));
        Assert.True(client.MaxObservedConcurrency <= maxConcurrency, $"Observed concurrency was {client.MaxObservedConcurrency}, exceeding max {maxConcurrency}");
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Test took too long: {stopwatch.ElapsedMilliseconds} ms");
    }

    private sealed class MockJitenApiClient : IJitenApiClient
    {
        private int _concurrency;
        public int MaxObservedConcurrency { get; private set; }
        public int SearchCallCount { get; private set; }
        public int DetailCallCount { get; private set; }
        public List<string> SearchQueries { get; } = [];
        public List<int> DetailDeckIds { get; } = [];

        public Func<string, Task<IReadOnlyList<JitenDeckDTO>>>? SearchHandler { get; set; }
        public Func<string, CancellationToken, Task<IReadOnlyList<JitenDeckDTO>>>? SearchHandlerWithToken { get; set; }
        public Func<int, Task<JitenDeckDetailDTO?>>? DetailHandler { get; set; }
        public Func<int, CancellationToken, Task<JitenDeckDetailDTO?>>? DetailHandlerWithToken { get; set; }

        public async Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref _concurrency);
            lock (SearchQueries)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
                SearchCallCount++;
                SearchQueries.Add(query);
            }

            try
            {
                if (SearchHandlerWithToken is not null)
                {
                    return await SearchHandlerWithToken(query, cancellationToken);
                }
                if (SearchHandler is not null)
                {
                    return await SearchHandler(query);
                }
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public async Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref _concurrency);
            lock (DetailDeckIds)
            {
                if (current > MaxObservedConcurrency)
                {
                    MaxObservedConcurrency = current;
                }
                DetailCallCount++;
                DetailDeckIds.Add(deckId);
            }

            try
            {
                if (DetailHandlerWithToken is not null)
                {
                    return await DetailHandlerWithToken(deckId, cancellationToken);
                }
                if (DetailHandler is not null)
                {
                    return await DetailHandler(deckId);
                }
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenFranchiseDTO?>(null);
    }
}
