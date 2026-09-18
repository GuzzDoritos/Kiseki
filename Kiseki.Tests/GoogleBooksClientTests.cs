using System.Net;
using System.Text;
using System.Text.Json;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Services.GoogleBooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kiseki.Tests;

public sealed class GoogleBooksClientTests
{
    private const string TestApiKey = "AIzaSyFakeTestKey123456789";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Client_WhenNotConfigured_ReturnsUnavailableWithoutHttpCalls(string? apiKey)
    {
        var handler = new TestStubHandler(_ => throw new InvalidOperationException("Should not be called"));
        var client = CreateClient(apiKey, handler);

        Assert.False(client.IsConfigured);
        Assert.Equal(GoogleBooksClientStatus.Unavailable, (await client.SearchVolumesAsync("test")).Status);
        Assert.Equal(GoogleBooksClientStatus.Unavailable, (await client.GetVolumeAsync("vol123")).Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SearchVolumesAsync_EncodesQueryParametersCorrectly()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestStubHandler(req =>
        {
            capturedRequest = req;
            return CreateJsonResponse(new GoogleBooksSearchResponseDto(0, []));
        });

        var client = CreateClient(TestApiKey, handler);
        var result = await client.SearchVolumesAsync("無職転生 1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        var pathAndQuery = capturedRequest.RequestUri?.PathAndQuery;
        Assert.NotNull(pathAndQuery);
        Assert.Contains("q=%E7%84%A1%E8%81%B7%E8%BB%A2%E7%94%9F%201", pathAndQuery);
        Assert.Contains($"key={TestApiKey}", pathAndQuery);
        Assert.Contains("printType=books", pathAndQuery);
        Assert.Contains("maxResults=20", pathAndQuery);
    }

    [Fact]
    public async Task SearchVolumesAsync_DeserializesVolumesSuccessfully()
    {
        var sampleResponse = new GoogleBooksSearchResponseDto(
            1,
            [
                new GoogleBooksVolumeDto(
                    "vol_123",
                    new GoogleBooksVolumeInfoDto
                    {
                        Title = "Sample Book",
                        Authors = ["Author One"],
                        Language = "ja",
                        PublishedDate = "2020-01-01",
                        ImageLinks = new GoogleBooksImageLinksDto
                        {
                            Thumbnail = "http://books.google.com/thumb.jpg",
                            ExtraLarge = "https://books.google.com/xlarge.jpg"
                        }
                    })
            ]);

        var handler = new TestStubHandler(_ => CreateJsonResponse(sampleResponse));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("Sample Book");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TotalItems);
        Assert.NotNull(result.Value.Items);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal("vol_123", item.Id);
        Assert.NotNull(item.VolumeInfo);
        Assert.Equal("Sample Book", item.VolumeInfo.Title);
        Assert.Equal("ja", item.VolumeInfo.Language);
        Assert.Equal("https://books.google.com/xlarge.jpg", item.VolumeInfo.ImageLinks?.GetPreferredImageLink());
    }

    [Fact]
    public async Task GetVolumeAsync_EncodesVolumeIdAndDeserializes()
    {
        HttpRequestMessage? capturedRequest = null;
        var sampleVolume = new GoogleBooksVolumeDto(
            "vol_abc",
            new GoogleBooksVolumeInfoDto
            {
                Title = "Single Volume",
                Language = "ja"
            });

        var handler = new TestStubHandler(req =>
        {
            capturedRequest = req;
            return CreateJsonResponse(sampleVolume);
        });

        var client = CreateClient(TestApiKey, handler);
        var result = await client.GetVolumeAsync("vol/abc");

        Assert.True(result.IsSuccess);
        Assert.Equal("vol_abc", result.Value!.Id);
        Assert.NotNull(capturedRequest);
        Assert.Contains("volumes/vol%2Fabc?", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetVolumeAsync_WhenNotFound_ReturnsTypedStatusWithoutRetry()
    {
        var handler = new TestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.GetVolumeAsync("nonexistent");

        Assert.Equal(GoogleBooksClientStatus.NotFound, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Client_When400BadRequest_ReturnsUnavailableWithoutRetry()
    {
        var handler = new TestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.Unavailable, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Client_WhenTransient503_RetriesAndSucceeds()
    {
        var attempt = 0;
        var handler = new TestStubHandler(_ =>
        {
            if (++attempt == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return CreateJsonResponse(new GoogleBooksSearchResponseDto(0, []));
        });

        var client = CreateClient(TestApiKey, handler);
        var result = await client.SearchVolumesAsync("query");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Client_When429RateLimited_RetriesWithRetryAfterHeader()
    {
        var attempt = 0;
        var handler = new TestStubHandler(_ =>
        {
            if (++attempt == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return response;
            }

            return CreateJsonResponse(new GoogleBooksSearchResponseDto(0, []));
        });

        var client = CreateClient(TestApiKey, handler);
        var result = await client.SearchVolumesAsync("query");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Client_When429RateLimitIsExhausted_ReturnsTypedStatus()
    {
        var handler = new TestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.RateLimited, result.Status);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Client_WhenPayloadIsMalformed_ReturnsTypedStatusWithoutRetry()
    {
        var handler = new TestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        });
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.MalformedResponse, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Client_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new AsyncTestStubHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        var client = CreateClient(TestApiKey, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SearchVolumesAsync("query", cancellation.Token));
    }

    [Fact]
    public async Task Client_InternalTimeout_ReturnsUnavailableWithoutRetry()
    {
        var handler = new AsyncTestStubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.Unavailable, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Client_WhenTransientFailsExhausted_ReturnsUnavailableAfterMaxRetries()
    {
        var handler = new TestStubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(TestApiKey, handler);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.Unavailable, result.Status);
        Assert.Equal(3, handler.RequestCount); // 1 initial + 2 retries = 3
    }

    [Fact]
    public async Task Client_SanitizesApiKeyInLoggedMessages()
    {
        var testLogger = new TestListLogger<GoogleBooksClient>();
        var handler = new TestStubHandler(_ =>
        {
            throw new HttpRequestException($"Network failed for https://www.googleapis.com/books/v1/volumes?key={TestApiKey}");
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://www.googleapis.com/books/v1/") };
        var options = Options.Create(new GoogleBooksOptions { ApiKey = TestApiKey });
        var client = new GoogleBooksClient(httpClient, options, testLogger);

        var result = await client.SearchVolumesAsync("query");

        Assert.Equal(GoogleBooksClientStatus.Unavailable, result.Status);
        Assert.NotEmpty(testLogger.LoggedMessages);
        Assert.All(testLogger.LoggedMessages, msg =>
        {
            Assert.DoesNotContain(TestApiKey, msg);
            Assert.Contains("[REDACTED]", msg);
        });
    }

    private static GoogleBooksClient CreateClient(
        string? apiKey,
        HttpMessageHandler handler,
        ILogger<GoogleBooksClient>? logger = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.googleapis.com/books/v1/")
        };
        var options = Options.Create(new GoogleBooksOptions { ApiKey = apiKey });
        return new GoogleBooksClient(httpClient, options, logger ?? NullLogger<GoogleBooksClient>.Instance);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T data)
    {
        var json = JsonSerializer.Serialize(data);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestStubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(handler(request));
        }
    }

    private sealed class AsyncTestStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return handler(request, cancellationToken);
        }
    }

    private sealed class TestListLogger<T> : ILogger<T>
    {
        public List<string> LoggedMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }
}
