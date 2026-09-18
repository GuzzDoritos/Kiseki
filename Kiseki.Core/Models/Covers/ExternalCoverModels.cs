namespace Kiseki.Core.Models.Covers;

public enum ExternalCoverProvider
{
    GoogleBooks,
    OpenLibrary
}

public sealed record ExternalCoverSelection(
    ExternalCoverProvider Provider,
    string CoverUrl,
    string ProviderItemId,
    string? AttributionLink = null);

public sealed record CoverEditionOption(
    string SelectionKey,
    ExternalCoverProvider Provider,
    string ProviderItemId,
    string? NormalizedIsbn,
    string CoverUrl,
    string? AttributionUrl,
    string Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    string? PublishedDate,
    int Width,
    int Height,
    bool IsLowResolution,
    IReadOnlyList<string> Evidence,
    bool IsSelected = false);

