namespace Kiseki.Core.Services.OpenLibrary;

public sealed class OpenLibraryOptions
{
    public const string OpenLibrary = "OpenLibrary";

    public string UserAgent { get; set; } = "Kiseki/1.0 (Japanese Immersion Tracker)";
    public string? ContactEmail { get; set; }
    public int MaxRequestsPerFiveMinutes { get; set; } = 100;
}

