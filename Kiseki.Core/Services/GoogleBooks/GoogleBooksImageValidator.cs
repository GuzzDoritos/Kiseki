using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Kiseki.Core.Services.GoogleBooks;

public sealed record ImageValidationResult(
    bool IsValid,
    string? ValidatedUrl = null,
    int Width = 0,
    int Height = 0,
    bool IsLowResolution = false,
    string? FailureReason = null);

public interface IGoogleBooksImageValidator
{
    Task<ImageValidationResult> ValidateCoverImageAsync(string? rawImageUrl, CancellationToken cancellationToken = default);
}

public interface ICoverImageValidator
{
    Task<ImageValidationResult> ValidateCoverImageAsync(
        string? rawImageUrl,
        IReadOnlySet<string>? customAllowedHosts,
        CancellationToken cancellationToken = default);
}

public sealed class GoogleBooksImageValidator : IGoogleBooksImageValidator, ICoverImageValidator
{
    private const int MaxUrlLength = 2048;
    private const int MaxRedirects = 3;
    private const long MaxImageBytes = 8 * 1024 * 1024; // 8 MiB
    private const int MaxDimension = 10_000;
    private const long MaxPixels = 50_000_000;
    private const int MinWidth = 250;
    private const int MinHeight = 350;
    private const int ThumbnailMinWidth = 100;
    private const int ThumbnailMinHeight = 140;
    private const double MinAspectRatio = 1.35;
    private const double MaxAspectRatio = 1.60;

    public static readonly HashSet<string> DefaultGoogleBooksAllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "books.google.com",
        "books.googleusercontent.com"
    };

    private static readonly HashSet<string> AllowedHosts = DefaultGoogleBooksAllowedHosts;

    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleBooksImageValidator> _logger;

    public GoogleBooksImageValidator(HttpClient httpClient, ILogger<GoogleBooksImageValidator> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ImageValidationResult> ValidateCoverImageAsync(
        string? rawImageUrl,
        CancellationToken cancellationToken = default) =>
        ValidateCoverImageAsync(rawImageUrl, null, cancellationToken);

    public async Task<ImageValidationResult> ValidateCoverImageAsync(
        string? rawImageUrl,
        IReadOnlySet<string>? customAllowedHosts,
        CancellationToken cancellationToken = default)
    {
        var effectiveHosts = customAllowedHosts ?? AllowedHosts;
        var hostListLabel = customAllowedHosts is null ? "Google Books host list" : "host list";

        if (string.IsNullOrWhiteSpace(rawImageUrl))
        {
            return new ImageValidationResult(false, FailureReason: "Image URL is empty.");
        }

        var normalizedUrl = rawImageUrl.Trim();
        if (normalizedUrl.Length > MaxUrlLength)
        {
            return new ImageValidationResult(false, FailureReason: $"Image URL exceeds maximum length of {MaxUrlLength}.");
        }

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var targetUri))
        {
            return new ImageValidationResult(false, FailureReason: "Image URL is not a valid absolute URI.");
        }

        // Scheme upgrade: only for explicitly allowed hosts
        if (targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveHosts.Contains(targetUri.Host))
            {
                var builder = new UriBuilder(targetUri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1
                };
                targetUri = builder.Uri;
            }
            else
            {
                return new ImageValidationResult(false, FailureReason: $"HTTP images from '{targetUri.Host}' are not allowed.");
            }
        }

        if (!targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new ImageValidationResult(false, FailureReason: "Image URL must use HTTPS.");
        }

        if (!effectiveHosts.Contains(targetUri.Host))
        {
            return new ImageValidationResult(false, FailureReason: $"Image host '{targetUri.Host}' is not in the allowed {hostListLabel}.");
        }

        // Fetch with manual redirect handling
        var redirectCount = 0;
        var currentUri = targetUri;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                redirectCount++;
                if (redirectCount > MaxRedirects)
                {
                    return new ImageValidationResult(false, FailureReason: "Image request exceeded maximum redirect count.");
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    return new ImageValidationResult(false, FailureReason: "Redirect response missing Location header.");
                }

                var resolved = new Uri(currentUri, location);
                if (!resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return new ImageValidationResult(false, FailureReason: "Redirect to non-HTTPS URL is prohibited.");
                }

                if (!effectiveHosts.Contains(resolved.Host))
                {
                    return new ImageValidationResult(false, FailureReason: $"Redirect host '{resolved.Host}' is not in the allowed {hostListLabel}.");
                }

                currentUri = resolved;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ImageValidationResult(false, FailureReason: $"Image request failed with status {(int)response.StatusCode}.");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType) || !AllowedMimeTypes.Contains(contentType))
            {
                return new ImageValidationResult(false, FailureReason: $"Unsupported image content type '{contentType}'.");
            }

            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaxImageBytes)
            {
                return new ImageValidationResult(false, FailureReason: "Declared image size exceeds 8 MiB limit.");
            }

            // Read stream enforcing max bytes
            byte[] imageBytes;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using var memoryStream = new MemoryStream();
                var buffer = new byte[8192];
                var totalRead = 0L;
                int read;

                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxImageBytes)
                    {
                        return new ImageValidationResult(false, FailureReason: "Streamed image size exceeded 8 MiB limit.");
                    }
                    memoryStream.Write(buffer, 0, read);
                }

                imageBytes = memoryStream.ToArray();
            }

            // Inspect image dimensions and format from headers
            if (!ImageMetadataReader.TryGetDimensions(imageBytes, out var width, out var height, out var isAnimated, out var failureReason))
            {
                return new ImageValidationResult(false, FailureReason: failureReason ?? "Could not read image metadata.");
            }

            if (isAnimated)
            {
                return new ImageValidationResult(false, FailureReason: "Animated images are not accepted.");
            }

            if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
            {
                return new ImageValidationResult(false, FailureReason: "Image dimensions exceed the safe processing limit.");
            }

            if (width < ThumbnailMinWidth)
            {
                return new ImageValidationResult(false, FailureReason: $"Image width {width}px is below minimum required {ThumbnailMinWidth}px.");
            }

            if (height < ThumbnailMinHeight)
            {
                return new ImageValidationResult(false, FailureReason: $"Image height {height}px is below minimum required {ThumbnailMinHeight}px.");
            }

            var aspectRatio = height / (double)width;
            if (aspectRatio < MinAspectRatio || aspectRatio > MaxAspectRatio)
            {
                return new ImageValidationResult(false, FailureReason: FormattableString.Invariant($"Image aspect ratio {aspectRatio:F2} is outside required range [{MinAspectRatio:F2}, {MaxAspectRatio:F2}]."));
            }

            var isLowResolution = width < MinWidth || height < MinHeight;
            return new ImageValidationResult(true, ValidatedUrl: currentUri.AbsoluteUri, Width: width, Height: height, IsLowResolution: isLowResolution);
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or
                HttpStatusCode.Found or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                (HttpStatusCode)308;
}

internal static class ImageMetadataReader
{
    public static bool TryGetDimensions(
        ReadOnlySpan<byte> bytes,
        out int width,
        out int height,
        out bool isAnimated,
        out string? failureReason)
    {
        width = 0;
        height = 0;
        isAnimated = false;
        failureReason = null;

        if (bytes.Length < 16)
        {
            failureReason = "Image byte stream too short.";
            return false;
        }

        // 1. Check PNG
        // 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 24 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            if (bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
            {
                width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
                if (width > 0 && height > 0)
                {
                    isAnimated = ContainsPngAnimationControlChunk(bytes);
                    return true;
                }
            }
            failureReason = "Malformed PNG IHDR chunk.";
            return false;
        }

        // 2. Check JPEG
        // FF D8
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            var offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }

                while (offset < bytes.Length && bytes[offset] == 0xFF)
                {
                    offset++;
                }

                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];

                if (marker is 0xD9 or 0xDA) // EOI or SOS
                {
                    break;
                }

                if (offset + 2 > bytes.Length) break;
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));

                // SOF markers: 0xC0..0xCF except 0xC4 (DHT), 0xC8 (JPG), 0xCC (DAC)
                var isSof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                if (isSof && offset + 7 <= bytes.Length)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                    if (width > 0 && height > 0)
                    {
                        return true;
                    }
                }

                offset += segmentLength;
            }

            failureReason = "Could not find SOF segment in JPEG.";
            return false;
        }

        // 3. Check WebP
        // RIFF .... WEBP
        if (bytes.Length >= 30 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            var chunkTag = System.Text.Encoding.ASCII.GetString(bytes.Slice(12, 4));
            if (chunkTag == "VP8 " && bytes.Length >= 30)
            {
                // Keyframe check
                if (bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
                {
                    width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
                    height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
                    if (width > 0 && height > 0)
                    {
                        return true;
                    }
                }
            }
            else if (chunkTag == "VP8L" && bytes.Length >= 25)
            {
                if (bytes[20] == 0x2F)
                {
                    var b1 = bytes[21];
                    var b2 = bytes[22];
                    var b3 = bytes[23];
                    var b4 = bytes[24];
                    width = (b1 | ((b2 & 0x3F) << 8)) + 1;
                    height = (((b2 >> 6) | (b3 << 2) | ((b4 & 0x0F) << 10))) + 1;
                    if (width > 0 && height > 0)
                    {
                        return true;
                    }
                }
            }
            else if (chunkTag == "VP8X" && bytes.Length >= 30)
            {
                var flags = bytes[20];
                isAnimated = (flags & 0x02) != 0;
                width = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
                height = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
                if (width > 0 && height > 0)
                {
                    return true;
                }
            }

            failureReason = "Unsupported or malformed WebP chunk.";
            return false;
        }

        failureReason = "Unrecognized or unsupported image raster format.";
        return false;
    }

    private static bool ContainsPngAnimationControlChunk(ReadOnlySpan<byte> bytes)
    {
        var offset = 8;
        while (offset + 12 <= bytes.Length)
        {
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var chunkLength = 12L + dataLength;
            if (chunkLength > bytes.Length - offset)
            {
                return false;
            }

            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return true;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                return false;
            }

            offset += (int)chunkLength;
        }

        return false;
    }
}
