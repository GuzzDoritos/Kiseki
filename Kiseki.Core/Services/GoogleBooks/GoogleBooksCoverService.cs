using System.Collections.Concurrent;
using Kiseki.Core.Models.Covers;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Services.Metadata;
using Kiseki.Core.Services.OpenLibrary;
using Microsoft.Extensions.Logging;

namespace Kiseki.Core.Services.GoogleBooks;

public interface IGoogleBooksCoverService
{
    bool IsConfigured { get; }

    Task<GoogleBooksCoverMatchResult> ResolveCoverAsync(
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default);

    Task<GoogleBooksCoverMatchResult> VerifyVolumeCoverAsync(
        string volumeId,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default);

    Task<GoogleBooksCoverMatchResult> VerifyCoverAsync(
        ExternalCoverProvider provider,
        string providerItemId,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default);
}

public sealed class GoogleBooksCoverService : IGoogleBooksCoverService
{
    private readonly IGoogleBooksClient _client;
    private readonly IGoogleBooksCoverMatcher _matcher;
    private readonly IGoogleBooksImageValidator _imageValidator;
    private readonly IOpenLibraryCoverClient? _openLibraryClient;
    private readonly ILogger<GoogleBooksCoverService> _logger;

    private static readonly SemaphoreSlim ConcurrencyGate = new(3, 3);
    private readonly ConcurrentDictionary<string, Lazy<Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>>>> _searchCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<GoogleBooksClientResult<GoogleBooksVolumeDto>>>> _volumeCache = new(StringComparer.Ordinal);

    public GoogleBooksCoverService(
        IGoogleBooksClient client,
        IGoogleBooksCoverMatcher matcher,
        IGoogleBooksImageValidator imageValidator,
        ILogger<GoogleBooksCoverService> logger,
        IOpenLibraryCoverClient? openLibraryClient = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _imageValidator = imageValidator ?? throw new ArgumentNullException(nameof(imageValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _openLibraryClient = openLibraryClient;
    }

    public bool IsConfigured => _client.IsConfigured;

    public async Task<GoogleBooksCoverMatchResult> ResolveCoverAsync(
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_client.IsConfigured)
        {
            return GoogleBooksCoverMatchResult.CreateNotConfigured();
        }

        var queries = _matcher.GenerateQueries(context);
        if (queries.Count == 0)
        {
            return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "No eligible search queries generated.");
        }

        await ConcurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var queryGroups = new List<GoogleBooksQueryResultGroup>();

            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = _searchCache.GetOrAdd(
                    query,
                    key => new Lazy<Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>>>(
                        () => _client.SearchVolumesAsync(key, cancellationToken),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                var clientResult = await request.Value;
                if (!clientResult.IsSuccess)
                {
                    _searchCache.TryRemove(query, out _);
                    if (clientResult.Status == GoogleBooksClientStatus.NotFound)
                    {
                        queryGroups.Add(new GoogleBooksQueryResultGroup(query, []));
                        continue;
                    }

                    return MapClientFailure(clientResult.Status, "Google Books search is unavailable.");
                }

                var response = clientResult.Value;
                var items = response?.Items ?? (IReadOnlyList<GoogleBooksVolumeDto>)[];
                queryGroups.Add(new GoogleBooksQueryResultGroup(query, items));
            }

            var matchedCandidate = _matcher.FindUniqueMatchingVolume(context, queryGroups, out var failureResult);
            if (failureResult is not null)
            {
                if (failureResult.Status == GoogleBooksMatchStatus.Ambiguous &&
                    failureResult.CandidateEditions is { Count: > 1 } candidates)
                {
                    var editionOptions = await BuildEditionOptionsAsync(candidates, context, cancellationToken);
                    if (editionOptions.Count > 0)
                    {
                        return GoogleBooksCoverMatchResult.CreateAmbiguous(
                            evidence: failureResult.Evidence,
                            warning: failureResult.Warning,
                            editionOptions: editionOptions,
                            candidateEditions: failureResult.CandidateEditions);
                    }
                }

                return failureResult;
            }

            if (matchedCandidate is null)
            {
                return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "No matching Google Books volume found.");
            }

            return await ValidateAndBuildMatchAsync(matchedCandidate, context, cancellationToken);
        }
        finally
        {
            ConcurrencyGate.Release();
        }
    }

    public async Task<GoogleBooksCoverMatchResult> VerifyVolumeCoverAsync(
        string volumeId,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(volumeId))
        {
            return GoogleBooksCoverMatchResult.CreateUnavailable("Invalid volume ID for verification.");
        }

        var normalizedVolumeId = volumeId.Trim();
        if (normalizedVolumeId.Length > 128 ||
            !normalizedVolumeId.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~'))
        {
            return GoogleBooksCoverMatchResult.CreateUnavailable("Invalid volume ID for verification.");
        }

        if (!_client.IsConfigured)
        {
            return GoogleBooksCoverMatchResult.CreateNotConfigured();
        }

        var queries = _matcher.GenerateQueries(context);
        if (queries.Count == 0)
        {
            return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "No eligible search queries generated for verification.");
        }

        await ConcurrencyGate.WaitAsync(cancellationToken);
        try
        {
            // 1. Repeat bounded volume-qualified searches server-side during confirmation
            var queryGroups = new List<GoogleBooksQueryResultGroup>();
            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clientResult = await _client.SearchVolumesAsync(query, cancellationToken);
                if (!clientResult.IsSuccess)
                {
                    if (clientResult.Status == GoogleBooksClientStatus.NotFound)
                    {
                        queryGroups.Add(new GoogleBooksQueryResultGroup(query, []));
                        continue;
                    }

                    return MapClientFailure(clientResult.Status, "Google Books search is unavailable during verification.");
                }

                var items = clientResult.Value?.Items ?? (IReadOnlyList<GoogleBooksVolumeDto>)[];
                queryGroups.Add(new GoogleBooksQueryResultGroup(query, items));
            }

            var searchMatched = _matcher.FindUniqueMatchingVolume(context, queryGroups, out var searchFailureResult);
            if (searchFailureResult is not null)
            {
                if (searchFailureResult.Status == GoogleBooksMatchStatus.Ambiguous &&
                    searchFailureResult.CandidateEditions is { Count: > 1 } candidates)
                {
                    var matchedCandidate = candidates.FirstOrDefault(c => string.Equals(c.Id, normalizedVolumeId, StringComparison.Ordinal));
                    if (matchedCandidate is not null)
                    {
                        searchMatched = new GoogleBooksMatchedVolume(
                            matchedCandidate,
                            GoogleBooksIdentityProof.ExplicitVolume,
                            ["Candidate verified from approved ambiguous editions."],
                            IsbnValidator.ExtractAndNormalizeIsbn(matchedCandidate.VolumeInfo?.IndustryIdentifiers));
                        searchFailureResult = null;
                    }
                }

                if (searchFailureResult is not null)
                {
                    return searchFailureResult;
                }
            }

            if (searchMatched is null)
            {
                return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "Search results did not reproduce a matching volume during verification.");
            }

            if (!string.Equals(searchMatched.Volume.Id, normalizedVolumeId, StringComparison.Ordinal))
            {
                return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "Search-verified volume ID did not match the reviewed volume.");
            }

            // 2. Fresh-fetch that exact volume ID
            var freshClientResult = await _client.GetVolumeAsync(normalizedVolumeId, cancellationToken);
            if (!freshClientResult.IsSuccess)
            {
                return MapClientFailure(
                    freshClientResult.Status,
                    freshClientResult.Status == GoogleBooksClientStatus.NotFound
                        ? "Google Books volume no longer exists."
                        : "Google Books volume verification is unavailable.");
            }

            var freshVolume = freshClientResult.Value;
            if (freshVolume is null)
            {
                return GoogleBooksCoverMatchResult.CreateUnavailable("Google Books volume could not be re-fetched.");
            }

            if (!string.Equals(freshVolume.Id, normalizedVolumeId, StringComparison.Ordinal))
            {
                return GoogleBooksCoverMatchResult.CreateNoMatch(
                    warning: "Re-fetched Google Books volume ID did not match the reviewed volume.");
            }

            // 3. Verify fresh volume satisfies all gates
            var verifiedMatched = _matcher.VerifyFreshVolume(context, freshVolume, searchMatched, out var verificationFailureResult);
            if (verificationFailureResult is not null)
            {
                return verificationFailureResult;
            }

            if (verifiedMatched is null)
            {
                return GoogleBooksCoverMatchResult.CreateNoMatch(warning: "Re-fetched volume did not satisfy matching gates.");
            }

            // 4. Validate image & build match result
            return await ValidateAndBuildMatchAsync(verifiedMatched, context, cancellationToken, ExternalCoverProvider.GoogleBooks);
        }
        finally
        {
            ConcurrencyGate.Release();
        }
    }

    public async Task<GoogleBooksCoverMatchResult> VerifyCoverAsync(
        ExternalCoverProvider provider,
        string providerItemId,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(providerItemId))
        {
            return GoogleBooksCoverMatchResult.CreateUnavailable("Invalid cover provider item identifier.");
        }

        if (provider == ExternalCoverProvider.OpenLibrary)
        {
            var normalizedIsbn = IsbnValidator.NormalizeAndValidateIsbn(providerItemId);
            if (string.IsNullOrWhiteSpace(normalizedIsbn))
            {
                return GoogleBooksCoverMatchResult.CreateUnavailable("Invalid ISBN for Open Library cover verification.");
            }

            if (_openLibraryClient is null)
            {
                return GoogleBooksCoverMatchResult.CreateUnavailable("Open Library client is not available.");
            }

            var olResult = await _openLibraryClient.GetCoverByIsbnAsync(normalizedIsbn, cancellationToken);
            if (olResult.IsMatched)
            {
                var evidence = new List<string>(olResult.Evidence ?? [])
                {
                    $"Verified Open Library cover for ISBN {normalizedIsbn}."
                };

                return GoogleBooksCoverMatchResult.CreateMatched(
                    normalizedIsbn,
                    olResult.CoverUrl!,
                    olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{normalizedIsbn}",
                    evidence,
                    proof: GoogleBooksIdentityProof.ExplicitVolume,
                    isLowResolution: olResult.IsLowResolution,
                    provider: ExternalCoverProvider.OpenLibrary);
            }

            return olResult.Status switch
            {
                OpenLibraryCoverStatus.RateLimited => GoogleBooksCoverMatchResult.CreateRateLimited(olResult.Warning),
                OpenLibraryCoverStatus.Unavailable => GoogleBooksCoverMatchResult.CreateUnavailable(olResult.Warning ?? "Open Library is temporarily unavailable."),
                OpenLibraryCoverStatus.InvalidImage => GoogleBooksCoverMatchResult.CreateInvalidImage(olResult.Warning ?? "Open Library cover image was invalid."),
                _ => GoogleBooksCoverMatchResult.CreateNoMatch(warning: "Open Library cover could not be reproduced during verification.")
            };
        }

        return await VerifyVolumeCoverAsync(providerItemId, context, cancellationToken);
    }

    private static GoogleBooksCoverMatchResult MapClientFailure(
        GoogleBooksClientStatus status,
        string unavailableWarning) =>
        status == GoogleBooksClientStatus.RateLimited
            ? GoogleBooksCoverMatchResult.CreateRateLimited()
            : GoogleBooksCoverMatchResult.CreateUnavailable(unavailableWarning);

    private async Task<IReadOnlyList<CoverEditionOption>> BuildEditionOptionsAsync(
        IReadOnlyList<GoogleBooksVolumeDto> candidates,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken)
    {
        var options = new List<CoverEditionOption>();

        foreach (var volume in candidates.Take(5))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawImageUrl = volume.VolumeInfo?.ImageLinks?.GetPreferredImageLink();
            if (string.IsNullOrWhiteSpace(rawImageUrl))
            {
                continue;
            }

            ImageValidationResult validation;
            try
            {
                validation = await _imageValidator.ValidateCoverImageAsync(rawImageUrl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Edition cover image validation failed for volume {VolumeId}: {Message}", volume.Id, ex.Message);
                continue;
            }

            if (!validation.IsValid)
            {
                continue;
            }

            var isbn = IsbnValidator.ExtractAndNormalizeIsbn(volume.VolumeInfo?.IndustryIdentifiers);
            CoverEditionOption? option = null;

            if (_openLibraryClient is not null && !string.IsNullOrWhiteSpace(isbn))
            {
                try
                {
                    var olResult = await _openLibraryClient.GetCoverByIsbnAsync(isbn, cancellationToken);
                    if (olResult.IsMatched)
                    {
                        var isOlBetter = (validation.IsLowResolution && !olResult.IsLowResolution) ||
                                         (!validation.IsLowResolution && !olResult.IsLowResolution &&
                                          (olResult.Width * olResult.Height >= validation.Width * validation.Height * 1.20));

                        if (isOlBetter)
                        {
                            var olEvidence = new List<string>
                            {
                                $"Open Library large cover validated at {olResult.Width}x{olResult.Height} for ISBN {isbn}."
                            };
                            if (olResult.IsLowResolution)
                            {
                                olEvidence.Add("Low-resolution Open Library cover tier accepted.");
                            }

                            option = new CoverEditionOption(
                                SelectionKey: $"ol_{isbn}",
                                Provider: ExternalCoverProvider.OpenLibrary,
                                ProviderItemId: isbn,
                                NormalizedIsbn: isbn,
                                CoverUrl: olResult.CoverUrl!,
                                AttributionUrl: olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{isbn}",
                                Title: volume.VolumeInfo?.Title ?? "Unknown Title",
                                Subtitle: volume.VolumeInfo?.Subtitle,
                                Authors: volume.VolumeInfo?.Authors ?? [],
                                PublishedDate: volume.VolumeInfo?.PublishedDate,
                                Width: olResult.Width,
                                Height: olResult.Height,
                                IsLowResolution: olResult.IsLowResolution,
                                Evidence: olEvidence);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Open Library lookup failed for ISBN {Isbn} during edition options build: {Message}", isbn, ex.Message);
                }
            }

            if (option is null)
            {
                var gbEvidence = new List<string>
                {
                    $"Google Books edition {volume.Id} validated at {validation.Width}x{validation.Height}."
                };
                if (validation.IsLowResolution)
                {
                    gbEvidence.Add("Low-resolution cover tier accepted.");
                }

                option = new CoverEditionOption(
                    SelectionKey: $"gb_{volume.Id}",
                    Provider: ExternalCoverProvider.GoogleBooks,
                    ProviderItemId: volume.Id,
                    NormalizedIsbn: isbn,
                    CoverUrl: validation.ValidatedUrl!,
                    AttributionUrl: $"https://books.google.com/books?id={Uri.EscapeDataString(volume.Id)}",
                    Title: volume.VolumeInfo?.Title ?? "Unknown Title",
                    Subtitle: volume.VolumeInfo?.Subtitle,
                    Authors: volume.VolumeInfo?.Authors ?? [],
                    PublishedDate: volume.VolumeInfo?.PublishedDate,
                    Width: validation.Width,
                    Height: validation.Height,
                    IsLowResolution: validation.IsLowResolution,
                    Evidence: gbEvidence);
            }

            options.Add(option);
        }

        return options
            .OrderBy(o => o.IsLowResolution)
            .ThenByDescending(o => o.Width * o.Height)
            .ThenBy(o => o.ProviderItemId, StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private async Task<GoogleBooksCoverMatchResult> ValidateAndBuildMatchAsync(
        GoogleBooksMatchedVolume matched,
        GoogleCoverLookupContext context,
        CancellationToken cancellationToken,
        ExternalCoverProvider? preferredProvider = null)
    {
        var candidate = matched.Volume;
        var rawImageUrl = candidate.VolumeInfo?.ImageLinks?.GetPreferredImageLink();
        var isbn = matched.NormalizedIsbn ?? IsbnValidator.ExtractAndNormalizeIsbn(candidate.VolumeInfo?.IndustryIdentifiers);

        if (_openLibraryClient is not null && !string.IsNullOrWhiteSpace(isbn) &&
            preferredProvider == ExternalCoverProvider.OpenLibrary)
        {
            var olResult = await _openLibraryClient.GetCoverByIsbnAsync(isbn, cancellationToken);
            if (olResult.IsMatched)
            {
                var olEvidence = new List<string>(matched.Evidence)
                {
                    $"Title: {candidate.VolumeInfo?.Title}",
                    $"Open Library cover validated at {olResult.Width}x{olResult.Height} for ISBN {isbn}."
                };
                return GoogleBooksCoverMatchResult.CreateMatched(
                    isbn,
                    olResult.CoverUrl!,
                    olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{isbn}",
                    olEvidence,
                    proof: matched.Proof,
                    isLowResolution: olResult.IsLowResolution,
                    provider: ExternalCoverProvider.OpenLibrary);
            }
        }

        if (string.IsNullOrWhiteSpace(rawImageUrl))
        {
            if (_openLibraryClient is not null && !string.IsNullOrWhiteSpace(isbn) && preferredProvider is null)
            {
                var olResult = await _openLibraryClient.GetCoverByIsbnAsync(isbn, cancellationToken);
                if (olResult.IsMatched)
                {
                    var olEvidence = new List<string>(matched.Evidence)
                    {
                        $"Title: {candidate.VolumeInfo?.Title}",
                        $"Open Library cover fallback validated at {olResult.Width}x{olResult.Height} for ISBN {isbn}."
                    };
                    return GoogleBooksCoverMatchResult.CreateMatched(
                        isbn,
                        olResult.CoverUrl!,
                        olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{isbn}",
                        olEvidence,
                        proof: matched.Proof,
                        isLowResolution: olResult.IsLowResolution,
                        provider: ExternalCoverProvider.OpenLibrary);
                }
            }

            return GoogleBooksCoverMatchResult.CreateInvalidImage("Volume does not provide a usable image link.");
        }

        ImageValidationResult validation;
        try
        {
            validation = await _imageValidator.ValidateCoverImageAsync(rawImageUrl, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Google Books cover image validation was unavailable ({ExceptionType}).",
                ex.GetType().Name);
            return GoogleBooksCoverMatchResult.CreateUnavailable(
                "Google Books cover image validation is temporarily unavailable.");
        }

        if (!validation.IsValid)
        {
            if (_openLibraryClient is not null && !string.IsNullOrWhiteSpace(isbn) && preferredProvider is null)
            {
                var olResult = await _openLibraryClient.GetCoverByIsbnAsync(isbn, cancellationToken);
                if (olResult.IsMatched)
                {
                    var olEvidence = new List<string>(matched.Evidence)
                    {
                        $"Title: {candidate.VolumeInfo?.Title}",
                        $"Open Library cover fallback validated at {olResult.Width}x{olResult.Height} for ISBN {isbn}."
                    };
                    return GoogleBooksCoverMatchResult.CreateMatched(
                        isbn,
                        olResult.CoverUrl!,
                        olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{isbn}",
                        olEvidence,
                        proof: matched.Proof,
                        isLowResolution: olResult.IsLowResolution,
                        provider: ExternalCoverProvider.OpenLibrary);
                }
            }

            return GoogleBooksCoverMatchResult.CreateInvalidImage(
                validation.FailureReason ?? "Image validation failed.");
        }

        if (_openLibraryClient is not null && !string.IsNullOrWhiteSpace(isbn) && preferredProvider is null)
        {
            try
            {
                var olResult = await _openLibraryClient.GetCoverByIsbnAsync(isbn, cancellationToken);
                if (olResult.IsMatched)
                {
                    var isOlBetter = (validation.IsLowResolution && !olResult.IsLowResolution) ||
                                     (!validation.IsLowResolution && !olResult.IsLowResolution &&
                                      (olResult.Width * olResult.Height >= validation.Width * validation.Height * 1.20));

                    if (isOlBetter)
                    {
                        var olEvidence = new List<string>(matched.Evidence)
                        {
                            $"Title: {candidate.VolumeInfo?.Title}",
                            $"Open Library large cover validated at {olResult.Width}x{olResult.Height} for ISBN {isbn}."
                        };
                        if (olResult.IsLowResolution)
                        {
                            olEvidence.Add("Low-resolution Open Library cover tier accepted.");
                        }

                        return GoogleBooksCoverMatchResult.CreateMatched(
                            isbn,
                            olResult.CoverUrl!,
                            olResult.AttributionUrl ?? $"https://openlibrary.org/isbn/{isbn}",
                            olEvidence,
                            proof: matched.Proof,
                            isLowResolution: olResult.IsLowResolution,
                            provider: ExternalCoverProvider.OpenLibrary);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Open Library comparison failed for ISBN {Isbn}: {Message}", isbn, ex.Message);
            }
        }

        var evidence = new List<string>(matched.Evidence)
        {
            $"Title: {candidate.VolumeInfo?.Title}",
            $"Language: {candidate.VolumeInfo?.Language}"
        };

        if (candidate.VolumeInfo?.Authors is { Count: > 0 } authors)
        {
            evidence.Add($"Authors: {string.Join(", ", authors)}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.VolumeInfo?.PublishedDate))
        {
            evidence.Add($"Published: {candidate.VolumeInfo.PublishedDate}");
        }

        evidence.Add($"Image: {validation.Width}x{validation.Height} ({(validation.Height / (double)validation.Width):F2} aspect ratio)");

        if (validation.IsLowResolution)
        {
            evidence.Add("Low-resolution cover tier accepted.");
        }

        var attributionLink = $"https://books.google.com/books?id={Uri.EscapeDataString(candidate.Id)}";

        return GoogleBooksCoverMatchResult.CreateMatched(
            candidate.Id,
            validation.ValidatedUrl!,
            attributionLink,
            evidence,
            proof: matched.Proof,
            isLowResolution: validation.IsLowResolution,
            provider: ExternalCoverProvider.GoogleBooks);
    }
}
