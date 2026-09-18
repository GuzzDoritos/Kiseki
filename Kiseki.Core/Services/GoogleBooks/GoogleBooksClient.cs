using System.Net;
using System.Text.Json;
using Kiseki.Core.Models.GoogleBooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kiseki.Core.Services.GoogleBooks;

public interface IGoogleBooksClient
{
    bool IsConfigured { get; }
    Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>> SearchVolumesAsync(string query, CancellationToken cancellationToken = default);
    Task<GoogleBooksClientResult<GoogleBooksVolumeDto>> GetVolumeAsync(string volumeId, CancellationToken cancellationToken = default);
}

public enum GoogleBooksClientStatus
{
    Success,
    NotFound,
    RateLimited,
    Unavailable,
    MalformedResponse
}

public sealed record GoogleBooksClientResult<T>(
    GoogleBooksClientStatus Status,
    T? Value = null) where T : class
{
    public bool IsSuccess => Status == GoogleBooksClientStatus.Success && Value is not null;

    public static GoogleBooksClientResult<T> Succeeded(T value) =>
        new(GoogleBooksClientStatus.Success, value);

    public static GoogleBooksClientResult<T> Failed(GoogleBooksClientStatus status) =>
        new(status);
}

public sealed class GoogleBooksClient : IGoogleBooksClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly GoogleBooksOptions _options;
    private readonly ILogger<GoogleBooksClient> _logger;

    public GoogleBooksClient(
        HttpClient httpClient,
        IOptions<GoogleBooksOptions> options,
        ILogger<GoogleBooksClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? new GoogleBooksOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>> SearchVolumesAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query))
        {
            return GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Failed(
                GoogleBooksClientStatus.Unavailable);
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        var relativeUrl = $"volumes?q={encodedQuery}&printType=books&orderBy=relevance&maxResults=20&key={Uri.EscapeDataString(_options.ApiKey!)}";

        return await ExecuteWithRetryAsync<GoogleBooksSearchResponseDto>(relativeUrl, cancellationToken);
    }

    public async Task<GoogleBooksClientResult<GoogleBooksVolumeDto>> GetVolumeAsync(
        string volumeId,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(volumeId))
        {
            return GoogleBooksClientResult<GoogleBooksVolumeDto>.Failed(
                GoogleBooksClientStatus.Unavailable);
        }

        var encodedVolumeId = Uri.EscapeDataString(volumeId.Trim());
        var relativeUrl = $"volumes/{encodedVolumeId}?key={Uri.EscapeDataString(_options.ApiKey!)}";

        return await ExecuteWithRetryAsync<GoogleBooksVolumeDto>(relativeUrl, cancellationToken);
    }

    private async Task<GoogleBooksClientResult<T>> ExecuteWithRetryAsync<T>(
        string relativeUrl,
        CancellationToken cancellationToken) where T : class
    {
        const int maxRetries = 2;
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
                    return value is null
                        ? GoogleBooksClientResult<T>.Failed(GoogleBooksClientStatus.MalformedResponse)
                        : GoogleBooksClientResult<T>.Succeeded(value);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return GoogleBooksClientResult<T>.Failed(GoogleBooksClientStatus.NotFound);
                }

                var isTransient = response.StatusCode == (HttpStatusCode)429 ||
                                  (int)response.StatusCode >= 500 && (int)response.StatusCode < 600;

                if (!isTransient || attempt > maxRetries)
                {
                    _logger.LogWarning("Google Books API request returned status {StatusCode}.", (int)response.StatusCode);
                    return GoogleBooksClientResult<T>.Failed(
                        response.StatusCode == (HttpStatusCode)429
                            ? GoogleBooksClientStatus.RateLimited
                            : GoogleBooksClientStatus.Unavailable);
                }

                var delay = GetRetryDelay(attempt);
                if (response.Headers.RetryAfter != null)
                {
                    if (response.Headers.RetryAfter.Delta.HasValue)
                    {
                        delay = response.Headers.RetryAfter.Delta.Value;
                    }
                    else if (response.Headers.RetryAfter.Date.HasValue)
                    {
                        var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                        if (diff > TimeSpan.Zero)
                        {
                            delay = diff;
                        }
                    }
                }

                if (delay > TimeSpan.FromSeconds(5))
                {
                    delay = TimeSpan.FromSeconds(5);
                }

                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Google Books request timed out on attempt {Attempt}.", attempt);
                return GoogleBooksClientResult<T>.Failed(GoogleBooksClientStatus.Unavailable);
            }
            catch (HttpRequestException ex)
            {
                if (attempt > maxRetries)
                {
                    _logger.LogWarning("Google Books HTTP request failed after {Attempt} attempts: {Message}", attempt, SanitizeMessage(ex.Message));
                    return GoogleBooksClientResult<T>.Failed(GoogleBooksClientStatus.Unavailable);
                }

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Google Books response JSON was malformed: {Message}", SanitizeMessage(ex.Message));
                return GoogleBooksClientResult<T>.Failed(GoogleBooksClientStatus.MalformedResponse);
            }
        }
    }

    private string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(_options.ApiKey) || string.IsNullOrEmpty(message))
        {
            return message;
        }

        return message.Replace(_options.ApiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(400 * attempt + Random.Shared.Next(0, 201));
}
