namespace Kiseki.Core.Services.GoogleBooks;

public sealed class GoogleBooksOptions
{
    public const string SectionName = "GoogleBooks";

    /// <summary>
    /// Google Books API key. When absent or blank, Google Books cover resolution is disabled.
    /// </summary>
    public string? ApiKey { get; set; }
}

