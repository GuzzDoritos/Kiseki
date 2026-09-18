using System.Text.Json.Serialization;

namespace Kiseki.Core.Models.GoogleBooks;

public sealed record GoogleBooksImageLinksDto(
    [property: JsonPropertyName("extraLarge")] string? ExtraLarge = null,
    [property: JsonPropertyName("large")] string? Large = null,
    [property: JsonPropertyName("medium")] string? Medium = null,
    [property: JsonPropertyName("small")] string? Small = null,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail = null,
    [property: JsonPropertyName("smallThumbnail")] string? SmallThumbnail = null)
{
    public string? GetPreferredImageLink() =>
        FirstNonEmpty(ExtraLarge, Large, Medium, Small, Thumbnail, SmallThumbnail);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed record GoogleBooksIndustryIdentifierDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("identifier")] string? Identifier);

public sealed record GoogleBooksVolumeInfoDto(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("subtitle")] string? Subtitle = null,
    [property: JsonPropertyName("authors")] IReadOnlyList<string>? Authors = null,
    [property: JsonPropertyName("publishedDate")] string? PublishedDate = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("industryIdentifiers")] IReadOnlyList<GoogleBooksIndustryIdentifierDto>? IndustryIdentifiers = null,
    [property: JsonPropertyName("imageLinks")] GoogleBooksImageLinksDto? ImageLinks = null,
    [property: JsonPropertyName("infoLink")] string? InfoLink = null,
    [property: JsonPropertyName("canonicalVolumeLink")] string? CanonicalVolumeLink = null);

public sealed record GoogleBooksVolumeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("volumeInfo")] GoogleBooksVolumeInfoDto? VolumeInfo);

public sealed record GoogleBooksSearchResponseDto(
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("items")] IReadOnlyList<GoogleBooksVolumeDto>? Items);

