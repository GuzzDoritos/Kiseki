using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Core.Services.OpenLibrary;

namespace Kiseki.Tests;

public sealed class OpenLibraryCoverClientTests
{
    private sealed class StubCoverImageValidator : ICoverImageValidator
    {
        public Func<string?, IReadOnlySet<string>?, CancellationToken, Task<ImageValidationResult>>? Handler { get; set; }
        public List<(string? Url, IReadOnlySet<string>? AllowedHosts)> Invocations { get; } = [];

        public Task<ImageValidationResult> ValidateCoverImageAsync(
            string? rawImageUrl,
            CancellationToken cancellationToken = default) =>
            ValidateCoverImageAsync(rawImageUrl, null, cancellationToken);

        public Task<ImageValidationResult> ValidateCoverImageAsync(
            string? rawImageUrl,
            IReadOnlySet<string>? customAllowedHosts,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add((rawImageUrl, customAllowedHosts));
            if (Handler is not null)
            {
                return Handler(rawImageUrl, customAllowedHosts, cancellationToken);
            }

            return Task.FromResult(new ImageValidationResult(false, FailureReason: "No stub handler."));
        }
    }

    [Fact]
    public async Task GetCoverByIsbnAsync_ConstructsExactOpenLibraryUrl_WithDefaultFalse()
    {
        var validator = new StubCoverImageValidator
        {
            Handler = (url, hosts, _) => Task.FromResult(new ImageValidationResult(
                true,
                ValidatedUrl: url,
                Width: 800,
                Height: 1200,
                IsLowResolution: false))
        };

        var client = new OpenLibraryCoverClient(validator);

        var result = await client.GetCoverByIsbnAsync("978-4-04-893341-4");

        Assert.True(result.IsMatched);
        Assert.Equal(OpenLibraryCoverStatus.Matched, result.Status);
        Assert.Equal("9784048933414", result.NormalizedIsbn);
        Assert.Equal("https://covers.openlibrary.org/b/isbn/9784048933414-L.jpg?default=false", result.CoverUrl);

        var call = Assert.Single(validator.Invocations);
        Assert.Equal("https://covers.openlibrary.org/b/isbn/9784048933414-L.jpg?default=false", call.Url);
        Assert.NotNull(call.AllowedHosts);
        Assert.Contains("covers.openlibrary.org", call.AllowedHosts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-isbn")]
    [InlineData("9784048933415")] // Invalid check digit
    public async Task GetCoverByIsbnAsync_InvalidIsbn_ReturnsNoMatchWithoutCallingValidator(string invalidIsbn)
    {
        var validator = new StubCoverImageValidator();
        var client = new OpenLibraryCoverClient(validator);

        var result = await client.GetCoverByIsbnAsync(invalidIsbn);

        Assert.Equal(OpenLibraryCoverStatus.NoMatch, result.Status);
        Assert.Empty(validator.Invocations);
    }

    [Fact]
    public async Task GetCoverByIsbnAsync_404Response_MapsToNoMatch()
    {
        var validator = new StubCoverImageValidator
        {
            Handler = (_, _, _) => Task.FromResult(new ImageValidationResult(
                false,
                FailureReason: "Image request returned status 404 (Not Found)."))
        };

        var client = new OpenLibraryCoverClient(validator);
        var result = await client.GetCoverByIsbnAsync("9784048933414");

        Assert.Equal(OpenLibraryCoverStatus.NoMatch, result.Status);
        Assert.Contains("No cover found", result.Warning);
    }

    [Theory]
    [InlineData("Image request returned status 429 (Too Many Requests).")]
    [InlineData("Image request returned status 403 (Forbidden).")]
    public async Task GetCoverByIsbnAsync_RateLimitOrForbidden_MapsToRateLimited(string errorReason)
    {
        var validator = new StubCoverImageValidator
        {
            Handler = (_, _, _) => Task.FromResult(new ImageValidationResult(false, FailureReason: errorReason))
        };

        var client = new OpenLibraryCoverClient(validator);
        var result = await client.GetCoverByIsbnAsync("9784048933414");

        Assert.Equal(OpenLibraryCoverStatus.RateLimited, result.Status);
        Assert.Contains("rate limit", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCoverByIsbnAsync_5xxResponseOrTimeout_MapsToUnavailable()
    {
        var validator = new StubCoverImageValidator
        {
            Handler = (_, _, _) => Task.FromResult(new ImageValidationResult(
                false,
                FailureReason: "Image request returned status 503 (Service Unavailable)."))
        };

        var client = new OpenLibraryCoverClient(validator);
        var result = await client.GetCoverByIsbnAsync("9784048933414");

        Assert.Equal(OpenLibraryCoverStatus.Unavailable, result.Status);
        Assert.Contains("unavailable", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCoverByIsbnAsync_InvalidImagePayload_MapsToInvalidImage()
    {
        var validator = new StubCoverImageValidator
        {
            Handler = (_, _, _) => Task.FromResult(new ImageValidationResult(
                false,
                FailureReason: "Image aspect ratio 0.8 is outside allowed range."))
        };

        var client = new OpenLibraryCoverClient(validator);
        var result = await client.GetCoverByIsbnAsync("9784048933414");

        Assert.Equal(OpenLibraryCoverStatus.InvalidImage, result.Status);
        Assert.Contains("aspect ratio", result.Warning);
    }

    [Fact]
    public async Task GetCoverByIsbnAsync_BatchCaching_DeduplicatesIdenticalIsbn()
    {
        var callCount = 0;
        var validator = new StubCoverImageValidator
        {
            Handler = (url, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new ImageValidationResult(
                    true,
                    ValidatedUrl: url,
                    Width: 600,
                    Height: 900));
            }
        };

        var client = new OpenLibraryCoverClient(validator);

        var first = await client.GetCoverByIsbnAsync("978-4-04-893341-4");
        var second = await client.GetCoverByIsbnAsync("9784048933414"); // same ISBN after normalization

        Assert.True(first.IsMatched);
        Assert.True(second.IsMatched);
        Assert.Equal(1, callCount);
        Assert.Single(validator.Invocations);
    }

    [Fact]
    public void OpenLibraryRateLimiter_SlidingWindow_EnforcesMaxRequestsPerFiveMinutes()
    {
        var limiter = new OpenLibraryRateLimiter(maxRequests: 3, window: TimeSpan.FromSeconds(10));

        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire()); // 4th request exceeds limit
    }
}
