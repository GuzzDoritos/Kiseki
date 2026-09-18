namespace Kiseki.Core.Entities;

public class MediaWork
{
    private const int MaxCoverUrlLength = 2048;
    public const int MaxCoverProviderItemIdLength = 128;
    private static readonly HashSet<string> GoogleBooksCoverHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "books.google.com",
        "books.googleusercontent.com"
    };

    private static readonly HashSet<string> OpenLibraryCoverHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "covers.openlibrary.org"
    };

    // Parameterless constructor for EF Core / Serialization
    protected MediaWork() { }

    // Primary domain constructor
    public MediaWork(
        string title,
        int? jitenDeckId = null,
        int? jitenCharCount = null,
        MediaType mediaType = MediaType.Book)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title;
        MediaType = mediaType;
        JitenDeckId = jitenDeckId;
        JitenCharacterCount = jitenCharCount;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public MediaType MediaType { get; set; } = MediaType.Book;

    public Guid? MediaSeriesId { get; set; }
    public MediaSeries? MediaSeries { get; set; }

    public int? JitenDeckId { get; set; }
    public int? JitenSubdeckId { get; private set; }
    public string? CoverUrl { get; private set; }
    public MediaCoverSource CoverSource { get; private set; } = MediaCoverSource.None;
    public string? CoverProviderItemId { get; private set; }

    public bool HasCover => !string.IsNullOrWhiteSpace(CoverUrl);
    public bool IsCoverProtected => CoverSource is MediaCoverSource.LegacyUnknown or MediaCoverSource.UserOverride;

    // Character Counts
    public int? JitenCharacterCount { get; set; }
    public int? TtsuCharacterCount { get; private set; }
    public int? ManualCharacterCountOverride { get; set; }

    public bool HasJitenLink => JitenDeckId.HasValue;
    public bool IsLinkedToJitenSubdeck => JitenSubdeckId.HasValue;

    // TTSU reflects the exact imported ebook; Jiten remains the metadata fallback.
    public int TotalCharacters => ManualCharacterCountOverride ?? TtsuCharacterCount ?? JitenCharacterCount ?? 0;

    // Status Override
    public bool IsCompleted { get; set; }

    public List<ImmersionLog> Logs { get; set; } = new();

    public void UpdateTtsuCharacterCount(int characterCount)
    {
        if (characterCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterCount), "TTSU character count must be positive.");
        }

        TtsuCharacterCount = characterCount;
    }

    public void LinkToJitenDeck(
        int deckId,
        int characterCount,
        string? coverUrl = null,
        MediaCoverSource coverSource = MediaCoverSource.JitenSpecific)
    {
        ValidateJitenLinkValues(deckId, characterCount);
        ValidateJitenCoverSource(coverSource);

        JitenDeckId = deckId;
        JitenSubdeckId = null;
        JitenCharacterCount = characterCount;
        ApplyJitenCover(coverUrl, coverSource);
    }

    public void LinkToJitenSubdeck(
        int parentDeckId,
        int subdeckId,
        int characterCount,
        string? coverUrl = null,
        MediaCoverSource coverSource = MediaCoverSource.JitenSpecific)
    {
        ValidateJitenLinkValues(parentDeckId, characterCount);

        if (subdeckId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subdeckId), "Subdeck ID must be positive.");
        }

        ValidateJitenCoverSource(coverSource);

        JitenDeckId = parentDeckId;
        JitenSubdeckId = subdeckId;
        JitenCharacterCount = characterCount;
        ApplyJitenCover(coverUrl, coverSource);
    }

    public void RemoveJitenLink()
    {
        JitenDeckId = null;
        JitenSubdeckId = null;
        JitenCharacterCount = null;

        if (CoverSource is MediaCoverSource.JitenSpecific or MediaCoverSource.JitenParentFallback)
        {
            CoverUrl = null;
            CoverSource = MediaCoverSource.None;
            CoverProviderItemId = null;
        }
    }

    public void UpdateCoverUrl(string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            throw new ArgumentException("Cover image URL cannot be empty.", nameof(coverUrl));
        }

        var normalized = NormalizeCoverUrl(coverUrl);
        if (normalized is null)
        {
            throw new ArgumentException("Cover image URL must be a valid HTTPS URL.", nameof(coverUrl));
        }

        CoverUrl = normalized;
        CoverSource = MediaCoverSource.UserOverride;
        CoverProviderItemId = null;
    }

    public void ApplyGoogleBooksCover(string coverUrl, string volumeId)
    {
        if (IsCoverProtected)
        {
            throw new InvalidOperationException("Cannot overwrite a protected cover.");
        }

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            throw new ArgumentException("Cover image URL cannot be empty.", nameof(coverUrl));
        }

        var normalizedUrl = NormalizeCoverUrl(coverUrl);
        if (normalizedUrl is null)
        {
            throw new ArgumentException("Cover image URL must be a valid HTTPS URL.", nameof(coverUrl));
        }

        var coverUri = new Uri(normalizedUrl, UriKind.Absolute);
        if (!GoogleBooksCoverHosts.Contains(coverUri.Host))
        {
            throw new ArgumentException(
                "Google Books cover URL must use an approved Google Books image host.",
                nameof(coverUrl));
        }

        var validatedVolumeId = ValidateProviderItemId(volumeId);

        CoverUrl = normalizedUrl;
        CoverSource = MediaCoverSource.GoogleBooks;
        CoverProviderItemId = validatedVolumeId;
    }

    public void ApplyOpenLibraryCover(string coverUrl, string isbn)
    {
        if (IsCoverProtected)
        {
            throw new InvalidOperationException("Cannot overwrite a protected cover.");
        }

        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            throw new ArgumentException("Cover image URL cannot be empty.", nameof(coverUrl));
        }

        var normalizedUrl = NormalizeCoverUrl(coverUrl);
        if (normalizedUrl is null)
        {
            throw new ArgumentException("Cover image URL must be a valid HTTPS URL.", nameof(coverUrl));
        }

        var coverUri = new Uri(normalizedUrl, UriKind.Absolute);
        if (!OpenLibraryCoverHosts.Contains(coverUri.Host))
        {
            throw new ArgumentException(
                "Open Library cover URL must use an approved Open Library image host.",
                nameof(coverUrl));
        }

        var validatedIsbn = ValidateProviderItemId(isbn);

        CoverUrl = normalizedUrl;
        CoverSource = MediaCoverSource.OpenLibrary;
        CoverProviderItemId = validatedIsbn;
    }

    public string? GetCoverAttributionUrl() => CoverSource switch
    {
        MediaCoverSource.GoogleBooks when !string.IsNullOrWhiteSpace(CoverProviderItemId) =>
            $"https://books.google.com/books?id={CoverProviderItemId}",
        MediaCoverSource.OpenLibrary when !string.IsNullOrWhiteSpace(CoverProviderItemId) =>
            $"https://openlibrary.org/isbn/{CoverProviderItemId}",
        _ => null
    };

    private void ApplyJitenCover(string? coverUrl, MediaCoverSource coverSource)
    {
        if (IsCoverProtected || CoverSource is MediaCoverSource.GoogleBooks or MediaCoverSource.OpenLibrary)
        {
            return;
        }

        if (coverSource == MediaCoverSource.None)
        {
            CoverUrl = null;
            CoverSource = MediaCoverSource.None;
            CoverProviderItemId = null;
            return;
        }

        var normalized = NormalizeCoverUrl(coverUrl);
        if (normalized is not null)
        {
            CoverUrl = normalized;
            CoverSource = coverSource;
            CoverProviderItemId = null;
        }
        else
        {
            CoverUrl = null;
            CoverSource = MediaCoverSource.None;
            CoverProviderItemId = null;
        }
    }

    private static string ValidateProviderItemId(string? volumeId)
    {
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            throw new ArgumentException("Provider item ID cannot be empty.", nameof(volumeId));
        }

        var trimmed = volumeId.Trim();
        if (trimmed.Length > MaxCoverProviderItemIdLength)
        {
            throw new ArgumentException(
                $"Provider item ID cannot exceed {MaxCoverProviderItemIdLength} characters.",
                nameof(volumeId));
        }

        if (!trimmed.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'))
        {
            throw new ArgumentException(
                "Provider item ID must be a valid URL-safe identifier.",
                nameof(volumeId));
        }

        return trimmed;
    }

    private static void ValidateJitenCoverSource(MediaCoverSource coverSource)
    {
        if (coverSource is not (MediaCoverSource.JitenSpecific or MediaCoverSource.JitenParentFallback or MediaCoverSource.None))
        {
            throw new ArgumentException(
                "Jiten cover provenance must be JitenSpecific, JitenParentFallback, or None.",
                nameof(coverSource));
        }
    }

    private static void ValidateJitenLinkValues(int deckId, int characterCount)
    {
        if (deckId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deckId), "Deck ID must be positive.");
        }

        if (characterCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterCount), "Character count cannot be negative.");
        }
    }

    private static string? NormalizeCoverUrl(string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl) ||
            coverUrl.Trim().Equals("nocover.jpg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = coverUrl.Trim();
        return normalized.Length <= MaxCoverUrlLength &&
               Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    public int CurrentCharactersRead => Logs.Sum(l => l.CharactersRead);

    // Dynamic Progress Calculation
    public double ProgressPercentage
    {
        get
        {
            // 1. If explicitly marked completed, force 100%
            if (IsCompleted) return 100.0;

            // 2. If no total characters are set, we cannot calculate %
            if (TotalCharacters == 0) return 0.0;

            // 3. Otherwise calculate percentage capped at 100%
            return Math.Min(100.0, ((double)CurrentCharactersRead / TotalCharacters) * 100.0);
        }
    }
}
