using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Services;

namespace Kiseki.Tests;

public sealed class JitenSelectionResolverTests
{
    [Fact]
    public async Task ResolveAsync_AcceptsFreshStandaloneDeck()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 42,
                    OriginalTitle = "Standalone Novel",
                    CharacterCount = 85_000,
                    CoverName = "https://cdn.jiten.moe/novel.jpg",
                    ChildrenDeckCount = 0
                },
                SubDecks = []
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 42, subdeckId: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.Success, result.Status);
        Assert.NotNull(result.Selection);
        Assert.Equal(42, result.Selection.DeckId);
        Assert.Null(result.Selection.SubdeckId);
        Assert.Equal("Standalone Novel", result.Selection.OriginalTitle);
        Assert.Equal(85_000, result.Selection.CharacterCount);
        Assert.Equal(JitenCoverEvidence.Specific, result.Selection.CoverEvidence);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsSubdeckContainedInParent()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Series",
                    CoverName = "https://cdn.jiten.moe/series.jpg",
                    ChildrenDeckCount = 2
                },
                SubDecks =
                [
                    new JitenDeckDTO
                    {
                        DeckId = 11,
                        OriginalTitle = "Volume 1",
                        CharacterCount = 90_000,
                        CoverName = "https://cdn.jiten.moe/v1.jpg"
                    },
                    new JitenDeckDTO
                    {
                        DeckId = 12,
                        OriginalTitle = "Volume 2",
                        CharacterCount = 95_000,
                        CoverName = ""
                    }
                ]
            }
        };

        var resolver = new JitenSelectionResolver(stub);

        // Subdeck with specific cover
        var result1 = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: 11);
        Assert.True(result1.IsSuccess);
        Assert.NotNull(result1.Selection);
        Assert.Equal(10, result1.Selection.DeckId);
        Assert.Equal(11, result1.Selection.SubdeckId);
        Assert.Equal("Volume 1", result1.Selection.OriginalTitle);
        Assert.Equal(90_000, result1.Selection.CharacterCount);
        Assert.Equal("https://cdn.jiten.moe/v1.jpg", result1.Selection.CoverUrl);
        Assert.Equal(JitenCoverEvidence.Specific, result1.Selection.CoverEvidence);

        // Subdeck falling back to parent cover
        var result2 = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: 12);
        Assert.True(result2.IsSuccess);
        Assert.NotNull(result2.Selection);
        Assert.Equal(12, result2.Selection.SubdeckId);
        Assert.Equal("https://cdn.jiten.moe/series.jpg", result2.Selection.CoverUrl);
        Assert.Equal(JitenCoverEvidence.ParentFallback, result2.Selection.CoverEvidence);
    }

    [Fact]
    public async Task ResolveAsync_RejectsSubdeckNotFoundInCollection()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 10, ChildrenDeckCount = 1 },
                SubDecks = [new JitenDeckDTO { DeckId = 11 }]
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: 999);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.SubdeckNotFound, result.Status);
        Assert.Null(result.Selection);
        Assert.Equal("The selected Jiten subdeck no longer exists.", result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveAsync_RejectsMismatchedParent()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO { DeckId = 99, ChildrenDeckCount = 0 },
                SubDecks = []
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.MismatchedParent, result.Status);
        Assert.Null(result.Selection);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public async Task ResolveAsync_RejectsNonPositiveIds(int parentDeckId, int? subdeckId)
    {
        var stub = new StubJitenApiClient();
        var resolver = new JitenSelectionResolver(stub);

        var result = await resolver.ResolveAsync(parentDeckId, subdeckId);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.InvalidDeckId, result.Status);
        Assert.Null(result.Selection);
        Assert.Null(stub.LastDetailDeckId);
    }

    [Fact]
    public async Task ResolveAsync_RejectsParentWhenItHasChildren()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Series with Children",
                    ChildrenDeckCount = 5
                },
                SubDecks = [new JitenDeckDTO { DeckId = 11 }]
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.ParentHasChildren, result.Status);
        Assert.Null(result.Selection);
        Assert.Equal("Cannot link a series deck directly when subdecks exist. Choose a specific subdeck.", result.ErrorMessage);
    }

    [Fact]
    public async Task ResolveAsync_RejectsParentWhenChildrenDeckCountIsZeroButSubdecksExist()
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Series",
                    ChildrenDeckCount = 0
                },
                SubDecks = [new JitenDeckDTO { DeckId = 11 }]
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.ParentHasChildren, result.Status);
        Assert.Null(result.Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_RejectsNegativeCharacterCounts(bool selectSubdeck)
    {
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    CharacterCount = selectSubdeck ? 0 : -1,
                    ChildrenDeckCount = selectSubdeck ? 1 : 0
                },
                SubDecks = selectSubdeck
                    ? [new JitenDeckDTO { DeckId = 11, CharacterCount = -1 }]
                    : []
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(10, selectSubdeck ? 11 : null);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.InvalidCharacterCount, result.Status);
        Assert.Null(result.Selection);
    }

    [Fact]
    public async Task ResolveAsync_RejectsWhenDetailIsNull()
    {
        var stub = new StubJitenApiClient
        {
            Detail = null
        };

        var resolver = new JitenSelectionResolver(stub);
        var result = await resolver.ResolveAsync(parentDeckId: 10, subdeckId: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(JitenSelectionStatus.DeckNotFound, result.Status);
        Assert.Null(result.Selection);
    }

    [Fact]
    public async Task ResolveAsync_PropagatesCancellation()
    {
        var stub = new StubJitenApiClient();
        var resolver = new JitenSelectionResolver(stub);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(parentDeckId: 10, subdeckId: null, cancellationToken: cts.Token));
    }

    [Fact]
    public void Resolve_PureOverload_ResolvesSubdeckAndStandaloneDirectly()
    {
        var stub = new StubJitenApiClient();
        var resolver = new JitenSelectionResolver(stub);

        var detail = new JitenDeckDetailDTO
        {
            MainDeck = new JitenDeckDTO
            {
                DeckId = 10,
                OriginalTitle = "Series",
                CoverName = "https://cdn.jiten.moe/series.jpg",
                ChildrenDeckCount = 1
            },
            SubDecks =
            [
                new JitenDeckDTO
                {
                    DeckId = 11,
                    OriginalTitle = "Volume 1",
                    CharacterCount = 90_000,
                    CoverName = "https://cdn.jiten.moe/v1.jpg"
                }
            ]
        };

        var result = resolver.Resolve(detail, parentDeckId: 10, subdeckId: 11);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Selection);
        Assert.Equal(10, result.Selection.DeckId);
        Assert.Equal(11, result.Selection.SubdeckId);
        Assert.Null(stub.LastDetailDeckId);
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public JitenDeckDetailDTO? Detail { get; init; }
        public int? LastDetailDeckId { get; private set; }

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(
            int deckId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDetailDeckId = deckId;
            return Task.FromResult(Detail);
        }

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(
            int deckId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenFranchiseDTO?>(null);
    }
}

