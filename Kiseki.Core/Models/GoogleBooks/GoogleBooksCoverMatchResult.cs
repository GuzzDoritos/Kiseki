using Kiseki.Core.Models.Covers;

namespace Kiseki.Core.Models.GoogleBooks;

public enum GoogleBooksIdentityProof
{
    None,
    ExplicitVolume,
    CrossQueryInferredVolume
}

public enum GoogleBooksMatchStatus
{
    Matched,
    NoMatch,
    Ambiguous,
    Unavailable,
    RateLimited,
    InvalidImage,
    TimedOut,
    NotConfigured
}

public sealed record GoogleBooksCoverMatchResult(
    GoogleBooksMatchStatus Status,
    string? VolumeId = null,
    string? CoverUrl = null,
    string? AttributionLink = null,
    IReadOnlyList<string>? Evidence = null,
    string? Warning = null,
    GoogleBooksIdentityProof Proof = GoogleBooksIdentityProof.None,
    bool IsLowResolution = false,
    IReadOnlyList<CoverEditionOption>? EditionOptions = null,
    IReadOnlyList<GoogleBooksVolumeDto>? CandidateEditions = null,
    ExternalCoverProvider Provider = ExternalCoverProvider.GoogleBooks)
{
    public bool IsMatched => Status == GoogleBooksMatchStatus.Matched &&
                             !string.IsNullOrWhiteSpace(VolumeId) &&
                             !string.IsNullOrWhiteSpace(CoverUrl);

    public static GoogleBooksCoverMatchResult CreateMatched(
        string volumeId,
        string coverUrl,
        string? attributionLink,
        IReadOnlyList<string> evidence,
        GoogleBooksIdentityProof proof = GoogleBooksIdentityProof.ExplicitVolume,
        bool isLowResolution = false,
        ExternalCoverProvider provider = ExternalCoverProvider.GoogleBooks) =>
        new(GoogleBooksMatchStatus.Matched, volumeId, coverUrl, attributionLink, evidence, Proof: proof, IsLowResolution: isLowResolution, Provider: provider);

    public static GoogleBooksCoverMatchResult CreateNoMatch(IReadOnlyList<string>? evidence = null, string? warning = null) =>
        new(GoogleBooksMatchStatus.NoMatch, Evidence: evidence, Warning: warning);

    public static GoogleBooksCoverMatchResult CreateAmbiguous(
        IReadOnlyList<string>? evidence = null,
        string? warning = null,
        IReadOnlyList<CoverEditionOption>? editionOptions = null,
        IReadOnlyList<GoogleBooksVolumeDto>? candidateEditions = null) =>
        new(GoogleBooksMatchStatus.Ambiguous,
            Evidence: evidence,
            Warning: warning ?? "Multiple matching Google Books volumes found.",
            EditionOptions: editionOptions,
            CandidateEditions: candidateEditions);

    public static GoogleBooksCoverMatchResult CreateUnavailable(string warning) =>
        new(GoogleBooksMatchStatus.Unavailable, Warning: warning);

    public static GoogleBooksCoverMatchResult CreateRateLimited(string? warning = null) =>
        new(GoogleBooksMatchStatus.RateLimited, Warning: warning ?? "Google Books rate limit exceeded.");

    public static GoogleBooksCoverMatchResult CreateInvalidImage(string reason, IReadOnlyList<string>? evidence = null) =>
        new(GoogleBooksMatchStatus.InvalidImage, Evidence: evidence, Warning: reason);

    public static GoogleBooksCoverMatchResult CreateTimedOut(string? warning = null) =>
        new(GoogleBooksMatchStatus.TimedOut, Warning: warning ?? "Google Books lookup timed out.");

    public static GoogleBooksCoverMatchResult CreateNotConfigured(string? warning = null) =>
        new(GoogleBooksMatchStatus.NotConfigured, Warning: warning ?? "Google Books is not configured.");
}

public sealed record GoogleBooksCoverSelection(
    string CoverUrl,
    string VolumeId,
    string? AttributionLink = null);

