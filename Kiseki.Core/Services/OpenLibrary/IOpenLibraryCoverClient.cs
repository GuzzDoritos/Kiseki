namespace Kiseki.Core.Services.OpenLibrary;

public enum OpenLibraryCoverStatus
{
    Matched,
    NoMatch,
    RateLimited,
    Unavailable,
    InvalidImage
}

public sealed record OpenLibraryCoverResult(
    OpenLibraryCoverStatus Status,
    string? NormalizedIsbn = null,
    string? CoverUrl = null,
    string? AttributionUrl = null,
    int Width = 0,
    int Height = 0,
    bool IsLowResolution = false,
    IReadOnlyList<string>? Evidence = null,
    string? Warning = null)
{
    public bool IsMatched => Status == OpenLibraryCoverStatus.Matched &&
                             !string.IsNullOrWhiteSpace(CoverUrl);

    public static OpenLibraryCoverResult Matched(
        string normalizedIsbn,
        string coverUrl,
        int width,
        int height,
        bool isLowResolution,
        IReadOnlyList<string> evidence) =>
        new(OpenLibraryCoverStatus.Matched,
            NormalizedIsbn: normalizedIsbn,
            CoverUrl: coverUrl,
            AttributionUrl: $"https://openlibrary.org/isbn/{normalizedIsbn}",
            Width: width,
            Height: height,
            IsLowResolution: isLowResolution,
            Evidence: evidence);

    public static OpenLibraryCoverResult NoMatch(string? warning = null) =>
        new(OpenLibraryCoverStatus.NoMatch, Warning: warning);

    public static OpenLibraryCoverResult RateLimited(string? warning = null) =>
        new(OpenLibraryCoverStatus.RateLimited, Warning: warning ?? "Open Library rate limit reached (100 requests per 5 minutes).");

    public static OpenLibraryCoverResult Unavailable(string? warning = null) =>
        new(OpenLibraryCoverStatus.Unavailable, Warning: warning ?? "Open Library is temporarily unavailable.");

    public static OpenLibraryCoverResult InvalidImage(string reason) =>
        new(OpenLibraryCoverStatus.InvalidImage, Warning: reason);
}

public interface IOpenLibraryCoverClient
{
    Task<OpenLibraryCoverResult> GetCoverByIsbnAsync(string rawIsbn, CancellationToken cancellationToken = default);
}

