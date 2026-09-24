using Kiseki.Core.Services;
using System.Net;
using System.Text;

namespace Kiseki.Tests;

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
        using var cancellationSource = new CancellationTokenSource();

        var detail = await client.GetDeckDetailAsync(10, cancellationSource.Token);

        Assert.NotNull(detail);
        Assert.Equal(3, detail.SubDecks.Count);
        Assert.Equal([101, 102, 103], detail.SubDecks.Select(deck => deck.DeckId));
        Assert.Equal(2, handler.RequestCount);
        Assert.All(handler.CancellationTokens, token => Assert.True(token.CanBeCanceled));
    }

    [Fact]
    public async Task GetDeckDetailAsync_ThrowsJitenHttpException_WithStatusCodeAndRetryAfter()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limit exceeded")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(15));
            return response;
        });

        var client = new JitenApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<JitenHttpException>(() => client.GetDeckDetailAsync(10));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(15), ex.RetryAfter);

        // Verify base HttpRequestException catch site compatibility
        HttpRequestException httpEx = ex;
        Assert.Equal(HttpStatusCode.TooManyRequests, httpEx.StatusCode);
    }

    [Fact]
    public async Task SearchBooksAsync_ThrowsJitenHttpException_On5xxServerError()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error")
            });

        var client = new JitenApiClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<JitenHttpException>(() => client.SearchBooksAsync("test"));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task SearchBooksBoundedAsync_CapsPaginationAtMaxResults()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var offset = request.RequestUri?.Query.Contains("offset=2") == true ? 2 : 0;
            var json = $$"""
                {
                  "data": [
                    { "deckId": {{offset + 1}}, "originalTitle": "Book {{offset + 1}}", "characterCount": 100 },
                    { "deckId": {{offset + 2}}, "originalTitle": "Book {{offset + 2}}", "characterCount": 200 }
                  ],
                  "totalItems": 10,
                  "pageSize": 2,
                  "currentOffset": {{offset}}
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new JitenApiClient(new HttpClient(handler));
        var results = await client.SearchBooksBoundedAsync("test", maxResults: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SearchBooksAsync_PerformsFullPagination()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var offset = request.RequestUri?.Query.Contains("offset=2") == true ? 2 : 0;
            var json = $$"""
                {
                  "data": [
                    { "deckId": {{offset + 1}}, "originalTitle": "Book {{offset + 1}}", "characterCount": 100 },
                    { "deckId": {{offset + 2}}, "originalTitle": "Book {{offset + 2}}", "characterCount": 200 }
                  ],
                  "totalItems": 4,
                  "pageSize": 2,
                  "currentOffset": {{offset}}
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var client = new JitenApiClient(new HttpClient(handler));
        var results = await client.SearchBooksAsync("test");

        Assert.Equal(4, results.Count);
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<CancellationToken> CancellationTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            CancellationTokens.Add(cancellationToken);
            return Task.FromResult(responder(request));
        }
    }
}
