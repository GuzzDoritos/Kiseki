using System.Buffers.Binary;
using System.Net;
using System.Text;
using Kiseki.Core.Services.GoogleBooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kiseki.Tests;

public sealed class GoogleBooksImageValidatorTests
{
    private static readonly byte[] ValidJpegBytes = CreateJpeg(500, 750);
    private static readonly byte[] ValidPngBytes = CreatePng(500, 750);
    private static readonly byte[] ValidWebpBytes = CreateWebp(500, 750, animated: false);
    private static readonly byte[] AnimatedWebpBytes = CreateWebp(500, 750, animated: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateCoverImageAsync_RejectsEmptyOrWhitespace(string? url)
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await validator.ValidateCoverImageAsync(url);

        Assert.False(result.IsValid);
        Assert.Equal("Image URL is empty.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsOverlongUrl()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var overlong = "https://books.google.com/" + new string('a', 2050);
        var result = await validator.ValidateCoverImageAsync(overlong);

        Assert.False(result.IsValid);
        Assert.Contains("exceeds maximum length", result.FailureReason);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path.jpg")]
    [InlineData("ftp://books.google.com/cover.jpg")]
    public async Task ValidateCoverImageAsync_RejectsInvalidOrUnsupportedSchemes(string url)
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await validator.ValidateCoverImageAsync(url);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("https://attacker.com/image.jpg")]
    [InlineData("https://books.google.com.attacker.com/image.jpg")]
    [InlineData("https://notbooks.google.com/image.jpg")]
    [InlineData("https://google.com/image.jpg")]
    [InlineData("https://books.googleusercontent.com.malicious.org/image.jpg")]
    public async Task ValidateCoverImageAsync_RejectsDisallowedHosts(string url)
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await validator.ValidateCoverImageAsync(url);

        Assert.False(result.IsValid);
        Assert.Contains("not in the allowed Google Books host list", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsHttpForDisallowedHost()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await validator.ValidateCoverImageAsync("http://attacker.com/image.jpg");

        Assert.False(result.IsValid);
        Assert.Contains("HTTP images from 'attacker.com' are not allowed", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_UpgradesHttpToHttps_ForAllowedHost()
    {
        HttpRequestMessage? capturedRequest = null;
        var validator = CreateValidator(req =>
        {
            capturedRequest = req;
            return CreateImageResponse(ValidJpegBytes, "image/jpeg");
        });

        var result = await validator.ValidateCoverImageAsync("http://books.google.com/books/content?id=123&printsec=frontcover");

        Assert.True(result.IsValid);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https", capturedRequest.RequestUri?.Scheme);
        Assert.Equal("https://books.google.com/books/content?id=123&printsec=frontcover", result.ValidatedUrl);
        Assert.Equal(500, result.Width);
        Assert.Equal(750, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_AcceptsValidJpeg()
    {
        var validator = CreateValidator(_ => CreateImageResponse(ValidJpegBytes, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.Equal(500, result.Width);
        Assert.Equal(750, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_AcceptsValidPng()
    {
        var validator = CreateValidator(_ => CreateImageResponse(ValidPngBytes, "image/png"));
        var result = await validator.ValidateCoverImageAsync("https://books.googleusercontent.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.Equal(500, result.Width);
        Assert.Equal(750, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_AcceptsValidWebp()
    {
        var validator = CreateValidator(_ => CreateImageResponse(ValidWebpBytes, "image/webp"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.Equal(500, result.Width);
        Assert.Equal(750, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsAnimatedWebp()
    {
        var validator = CreateValidator(_ => CreateImageResponse(AnimatedWebpBytes, "image/webp"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Equal("Animated images are not accepted.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsAnimatedPng()
    {
        var validator = CreateValidator(_ => CreateImageResponse(CreateAnimatedPng(500, 750), "image/png"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Equal("Animated images are not accepted.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsExtremeDimensions()
    {
        var validator = CreateValidator(_ => CreateImageResponse(CreatePng(10_000, 15_000), "image/png"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Equal("Image dimensions exceed the safe processing limit.", result.FailureReason);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task ValidateCoverImageAsync_RejectsDisallowedMimeTypes(string mimeType)
    {
        var validator = CreateValidator(_ => CreateImageResponse(ValidJpegBytes, mimeType));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("Unsupported image content type", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsNonSuccessStatusCode()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("failed with status 404", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsDeclaredContentLengthExceeding8MiB()
    {
        var response = CreateImageResponse(ValidJpegBytes, "image/jpeg");
        response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;

        var validator = CreateValidator(_ => response);
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Equal("Declared image size exceeds 8 MiB limit.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsStreamedBytesExceeding8MiB()
    {
        var validator = CreateValidator(_ =>
        {
            var largeStream = new NonSeekableStream(new MemoryStream(new byte[8 * 1024 * 1024 + 10]));
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(largeStream)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return resp;
        });

        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Equal("Streamed image size exceeded 8 MiB limit.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsHeightBelow140px()
    {
        var shortJpeg = CreateJpeg(100, 135); // height 135 < 140
        var validator = CreateValidator(_ => CreateImageResponse(shortJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("below minimum required", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsWidthBelow100px()
    {
        var narrowJpeg = CreateJpeg(90, 140); // width 90 < 100
        var validator = CreateValidator(_ => CreateImageResponse(narrowJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("below minimum required", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_AcceptsGoogleBooksSmallTier()
    {
        var smallJpeg = CreateJpeg(267, 400); // aspect ratio 1.5, width 267 >= 250, height 400 >= 350
        var validator = CreateValidator(_ => CreateImageResponse(smallJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.False(result.IsLowResolution);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_Accepts128pxThumbnailAsLowResolution()
    {
        var thumbJpeg = CreateJpeg(128, 192); // 128px thumbnail, aspect ratio 1.50
        var validator = CreateValidator(_ => CreateImageResponse(thumbJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.True(result.IsLowResolution);
        Assert.Equal(128, result.Width);
        Assert.Equal(192, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_Rejects80pxSmallThumbnail()
    {
        var smallThumbJpeg = CreateJpeg(80, 120); // 80px smallThumbnail (< 100px min width)
        var validator = CreateValidator(_ => CreateImageResponse(smallThumbJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("below minimum required", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsAspectRatioBelow135()
    {
        var wideJpeg = CreateJpeg(600, 600); // aspect ratio 1.0 < 1.35
        var validator = CreateValidator(_ => CreateImageResponse(wideJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("outside required range [1.35, 1.60]", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsAspectRatioAbove160()
    {
        var tallJpeg = CreateJpeg(400, 800); // aspect ratio 2.0 > 1.60
        var validator = CreateValidator(_ => CreateImageResponse(tallJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.False(result.IsValid);
        Assert.Contains("outside required range [1.35, 1.60]", result.FailureReason);
    }

    [Theory]
    [InlineData(600, 810)] // 810 / 600 = 1.35
    [InlineData(500, 800)] // 800 / 500 = 1.60
    public async Task ValidateCoverImageAsync_AcceptsAspectRatioAtExactBoundaries(int width, int height)
    {
        var boundaryJpeg = CreateJpeg(width, height);
        var validator = CreateValidator(_ => CreateImageResponse(boundaryJpeg, "image/jpeg"));
        var result = await validator.ValidateCoverImageAsync("https://books.google.com/books/content?id=abc");

        Assert.True(result.IsValid);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_FollowsRedirectsUpTo3TimesSuccessfully()
    {
        var callCount = 0;
        var validator = CreateValidator(req =>
        {
            callCount++;
            if (callCount < 4)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri($"https://books.googleusercontent.com/step{callCount}");
                return redirect;
            }

            return CreateImageResponse(ValidJpegBytes, "image/jpeg");
        });

        var result = await validator.ValidateCoverImageAsync("https://books.google.com/start");

        Assert.True(result.IsValid);
        Assert.Equal(4, callCount); // 1 initial + 3 redirects
        Assert.Equal("https://books.googleusercontent.com/step3", result.ValidatedUrl);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsMoreThanThreeRedirects()
    {
        var callCount = 0;
        var validator = CreateValidator(req =>
        {
            callCount++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri($"https://books.googleusercontent.com/step{callCount}");
            return redirect;
        });

        var result = await validator.ValidateCoverImageAsync("https://books.google.com/start");

        Assert.False(result.IsValid);
        Assert.Equal("Image request exceeded maximum redirect count.", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsRedirectToDisallowedHost()
    {
        var validator = CreateValidator(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://evil.com/cover.jpg");
            return redirect;
        });

        var result = await validator.ValidateCoverImageAsync("https://books.google.com/start");

        Assert.False(result.IsValid);
        Assert.Contains("Redirect host 'evil.com' is not in the allowed Google Books host list", result.FailureReason);
    }

    [Fact]
    public async Task ValidateCoverImageAsync_RejectsRedirectToNonHttps()
    {
        var validator = CreateValidator(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://books.google.com/cover.jpg");
            return redirect;
        });

        var result = await validator.ValidateCoverImageAsync("https://books.google.com/start");

        Assert.False(result.IsValid);
        Assert.Equal("Redirect to non-HTTPS URL is prohibited.", result.FailureReason);
    }

    private static GoogleBooksImageValidator CreateValidator(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new TestStubHandler(handler));
        return new GoogleBooksImageValidator(httpClient, NullLogger<GoogleBooksImageValidator>.Instance);
    }

    private static HttpResponseMessage CreateImageResponse(byte[] bytes, string mimeType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        return response;
    }

    private static byte[] CreatePng(int width, int height)
    {
        var bytes = new byte[32];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] CreateAnimatedPng(int width, int height)
    {
        var bytes = new byte[53];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(33, 4), 8);
        Encoding.ASCII.GetBytes("acTL").CopyTo(bytes, 37);
        return bytes;
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        var bytes = new byte[32];
        bytes[0] = 0xFF; bytes[1] = 0xD8;
        bytes[2] = 0xFF; bytes[3] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), 17);
        bytes[6] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7, 2), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9, 2), (ushort)width);
        return bytes;
    }

    private static byte[] CreateWebp(int width, int height, bool animated = false)
    {
        var bytes = new byte[32];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 24);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("VP8X").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 10);
        bytes[20] = (byte)(animated ? 0x02 : 0x00);
        var wMinus1 = width - 1;
        var hMinus1 = height - 1;
        bytes[24] = (byte)(wMinus1 & 0xFF);
        bytes[25] = (byte)((wMinus1 >> 8) & 0xFF);
        bytes[26] = (byte)((wMinus1 >> 16) & 0xFF);
        bytes[27] = (byte)(hMinus1 & 0xFF);
        bytes[28] = (byte)((hMinus1 >> 8) & 0xFF);
        bytes[29] = (byte)((hMinus1 >> 16) & 0xFF);
        return bytes;
    }

    private sealed class TestStubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
