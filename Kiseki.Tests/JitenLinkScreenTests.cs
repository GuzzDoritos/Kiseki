using Kiseki.Console.Screens;
using Kiseki.Core.DTOs;
using Kiseki.Core.Models;
using Kiseki.Core.Services;

namespace Kiseki.Tests;

public sealed class JitenLinkScreenTests
{
    [Fact]
    public void GetSubdeckPromptChoices_NeverOffersLinkEntireDeck_RegardlessOfDeckState()
    {
        var choices = JitenLinkScreen.GetSubdeckPromptChoices();

        Assert.DoesNotContain("Link the entire deck", choices);
        Assert.Equal(["Choose a subdeck", "Cancel"], choices);
    }

    [Fact]
    public void SubdeckPromptChoices_NeverOffersLinkEntireDeck_EvenWhenSearchReportedZeroChildren()
    {
        // Regression test: A search result may report ChildrenDeckCount == 0, but JitenSelectionResolver
        // reveals that the deck actually has children (ParentHasChildren). In that case, the subdeck-only
        // decision path must only present subdeck choices, never offering "Link the entire deck".
        var staleSearchDeck = new JitenDeckDTO
        {
            DeckId = 10,
            OriginalTitle = "Deceptive Series",
            ChildrenDeckCount = 0
        };

        // The subdeck path uses GetSubdeckPromptChoices(), ensuring the stale DTO cannot contaminate the choices.
        var choices = JitenLinkScreen.GetSubdeckPromptChoices();

        Assert.DoesNotContain("Link the entire deck", choices);
        Assert.Contains("Choose a subdeck", choices);
        Assert.Contains("Cancel", choices);
    }

    [Fact]
    public async Task Resolver_PreventsLinkingParentWithChildren_EvenIfSearchClaimedNoChildren()
    {
        // Simulate a search result that had ChildrenDeckCount = 0,
        // but fresh detail returns ChildrenDeckCount = 2 or has subdecks
        var stub = new StubJitenApiClient
        {
            Detail = new JitenDeckDetailDTO
            {
                MainDeck = new JitenDeckDTO
                {
                    DeckId = 10,
                    OriginalTitle = "Misleading Series",
                    ChildrenDeckCount = 2
                },
                SubDecks =
                [
                    new JitenDeckDTO { DeckId = 11, OriginalTitle = "Vol 1" }
                ]
            }
        };

        var resolver = new JitenSelectionResolver(stub);
        var resolution = await resolver.ResolveAsync(10, subdeckId: null);

        Assert.False(resolution.IsSuccess);
        Assert.Equal(JitenSelectionStatus.ParentHasChildren, resolution.Status);
        Assert.Null(resolution.Selection);
    }

    private sealed class StubJitenApiClient : IJitenApiClient
    {
        public JitenDeckDetailDTO? Detail { get; init; }

        public Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JitenDeckDTO>>([]);

        public Task<JitenDeckDetailDTO?> GetDeckDetailAsync(
            int deckId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Detail);

        public Task<JitenFranchiseDTO?> GetFranchiseAsync(
            int deckId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<JitenFranchiseDTO?>(null);
    }
}

