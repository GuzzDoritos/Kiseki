using System.Collections.Concurrent;
using System.Net;
using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Core.Services.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kiseki.Core.Services.OpenLibrary;

public sealed class OpenLibraryCoverClient : IOpenLibraryCoverClient
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers.openlibrary.org"
    };

    private readonly ICoverImageValidator _imageValidator;
    private readonly OpenLibraryRateLimiter _rateLimiter;
    private readonly ILogger<OpenLibraryCoverClient> _logger;
    private readonly SemaphoreSlim _semaphore = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<OpenLibraryCoverResult>>> _cache = new(StringComparer.Ordinal);

    public OpenLibraryCoverClient(
        ICoverImageValidator imageValidator,
        IOptions<OpenLibraryOptions>? options = null,
        ILogger<OpenLibraryCoverClient>? logger = null,
        OpenLibraryRateLimiter? rateLimiter = null)
    {
        _imageValidator = imageValidator ?? throw new ArgumentNullException(nameof(imageValidator));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenLibraryCoverClient>.Instance;
        var opts = options?.Value ?? new OpenLibraryOptions();
        _rateLimiter = rateLimiter ?? new OpenLibraryRateLimiter(opts.MaxRequestsPerFiveMinutes);
    }

    public async Task<OpenLibraryCoverResult> GetCoverByIsbnAsync(string rawIsbn, CancellationToken cancellationToken = default)
    {
        var normalizedIsbn = IsbnValidator.NormalizeAndValidateIsbn(rawIsbn);
        if (string.IsNullOrWhiteSpace(normalizedIsbn))
        {
            return OpenLibraryCoverResult.NoMatch("Invalid ISBN format or check digit.");
        }

        var lazy = _cache.GetOrAdd(
            normalizedIsbn,
            isbn => new Lazy<Task<OpenLibraryCoverResult>>(
                () => FetchAndValidateCoverAsync(isbn, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value;
        }
        catch
        {
            _cache.TryRemove(normalizedIsbn, out _);
            throw;
        }
    }

    private async Task<OpenLibraryCoverResult> FetchAndValidateCoverAsync(string normalizedIsbn, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!_rateLimiter.TryAcquire())
            {
                _logger.LogWarning("Open Library rate limit reached (100 requests per 5 minutes) for ISBN {Isbn}", normalizedIsbn);
                return OpenLibraryCoverResult.RateLimited("Open Library rate limit reached (100 requests per 5 minutes).");
            }

            var url = $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(normalizedIsbn)}-L.jpg?default=false";

            ImageValidationResult validation;
            try
            {
                validation = await _imageValidator.ValidateCoverImageAsync(url, AllowedHosts, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Open Library request failed for ISBN {Isbn}", normalizedIsbn);
                return OpenLibraryCoverResult.Unavailable("Open Library is temporarily unavailable.");
            }

            if (!validation.IsValid)
            {
                var reason = validation.FailureReason ?? string.Empty;

                if (reason.Contains("status 404", StringComparison.OrdinalIgnoreCase))
                {
                    return OpenLibraryCoverResult.NoMatch($"No cover found on Open Library for ISBN {normalizedIsbn}.");
                }

                if (reason.Contains("status 429", StringComparison.OrdinalIgnoreCase) ||
                    reason.Contains("status 403", StringComparison.OrdinalIgnoreCase))
                {
                    return OpenLibraryCoverResult.RateLimited("Open Library rate limit reached or access restricted.");
                }

                if (reason.Contains("status 5", StringComparison.OrdinalIgnoreCase))
                {
                    return OpenLibraryCoverResult.Unavailable("Open Library is temporarily unavailable.");
                }

                return OpenLibraryCoverResult.InvalidImage(reason);
            }

            var evidence = new List<string>
            {
                $"Open Library large cover validated at {validation.Width}x{validation.Height} for ISBN {normalizedIsbn}."
            };

            if (validation.IsLowResolution)
            {
                evidence.Add("Low-resolution Open Library cover tier accepted.");
            }

            return OpenLibraryCoverResult.Matched(
                normalizedIsbn,
                validation.ValidatedUrl!,
                validation.Width,
                validation.Height,
                validation.IsLowResolution,
                evidence);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

