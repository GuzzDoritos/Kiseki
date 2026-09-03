using Core.Services;
using System.Net;
using System.Text;

namespace Tests;

public class JitenApiClientTests
{
    [Fact]
    public async Task GetDeckDetailAsync_LoadsEverySubdeckPage()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var offset = request.RequestUri?.Query.Contains("offset=2") == true
                ? 2
                : 0;

            var subdecks = offset == 0
                ? """
                  [
                    { "deckId": 101, "originalTitle": "Volume 1", "characterCount": 100 },
                    { "deckId": 102, "originalTitle": "Volume 2", "characterCount": 200 }
                  ]
                  """
                : """
                  [
                    { "deckId": 103, "originalTitle": "Volume 3", "characterCount": 300 }
                  ]
                  """;

            var json = $$"""
                {
                  "data": {
                    "parentDeck": null,
                    "mainDeck": {
                      "deckId": 10,
                      "originalTitle": "Series",
                      "characterCount": 600,
                      "childrenDeckCount": 3
                    },
                    "subDecks": {{subdecks}}
                  },
                  "totalItems": 3,
                  "pageSize": 2,
                  "currentOffset": {{offset}}
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler);
        var client = new JitenApiClient(httpClient);

        var detail = await client.GetDeckDetailAsync(10);

        Assert.NotNull(detail);
        Assert.Equal(3, detail.SubDecks.Count);
        Assert.Equal([101, 102, 103], detail.SubDecks.Select(deck => deck.DeckId));
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
