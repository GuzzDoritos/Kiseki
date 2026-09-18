using Kiseki.Core.Models.Covers;
using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Core.Services.Metadata;
using Kiseki.Core.Services.OpenLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kiseki.Tests;

public sealed class GoogleBooksCoverServiceTests
{
    [Fact]
    public async Task ResolveCoverAsync_MapsExhaustedRateLimitToTypedOutcome()
    {
        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Failed(
                GoogleBooksClientStatus.RateLimited)
        };
        var service = CreateService(client);

        var result = await service.ResolveCoverAsync(CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.RateLimited, result.Status);
        Assert.Contains("rate limit", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyVolumeCoverAsync_RejectsProviderResponseWithDifferentVolumeId()
    {
        var reviewedCandidate = new GoogleBooksVolumeDto(
            "reviewed_id",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto(
                    Thumbnail: "https://books.google.com/cover.jpg")));
        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [reviewedCandidate])),
            VolumeResult = GoogleBooksClientResult<GoogleBooksVolumeDto>.Succeeded(
                new GoogleBooksVolumeDto(
                    "different_id",
                    new GoogleBooksVolumeInfoDto(
                        Title: "スパイ教室 1",
                        Language: "ja",
                        ImageLinks: new GoogleBooksImageLinksDto(
                            Thumbnail: "https://books.google.com/cover.jpg"))))
        };
        var matcher = new StubMatcher { Match = reviewedCandidate };
        var service = CreateService(client, matcher);

        var result = await service.VerifyVolumeCoverAsync("reviewed_id", CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.NoMatch, result.Status);
        Assert.Contains("did not match", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad/id")]
    [InlineData("日本語")]
    public async Task VerifyVolumeCoverAsync_RejectsUnsafeIdWithoutProviderCall(string volumeId)
    {
        var client = new StubClient();
        var service = CreateService(client);

        var result = await service.VerifyVolumeCoverAsync(volumeId, CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Unavailable, result.Status);
        Assert.Equal(0, client.VolumeCalls);
    }

    [Fact]
    public async Task ResolveCoverAsync_ImageTransportFailureBecomesUnavailableOutcome()
    {
        var candidate = new GoogleBooksVolumeDto(
            "matching_id",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto(
                    Thumbnail: "https://books.google.com/cover.jpg")));
        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [candidate]))
        };
        var matcher = new StubMatcher { Match = candidate };
        var validator = new StubImageValidator
        {
            Handler = () => throw new HttpRequestException("transport failed")
        };
        var service = CreateService(client, matcher, validator);

        var result = await service.ResolveCoverAsync(CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Unavailable, result.Status);
        Assert.Contains("temporarily unavailable", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveCoverAsync_NeverExceedsThreeConcurrentProviderOperations()
    {
        var sync = new object();
        var active = 0;
        var maximumActive = 0;
        var client = new StubClient
        {
            SearchHandler = async (_, token) =>
            {
                lock (sync)
                {
                    active++;
                    maximumActive = Math.Max(maximumActive, active);
                }

                try
                {
                    await Task.Delay(50, token);
                    return GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                        new GoogleBooksSearchResponseDto(0, []));
                }
                finally
                {
                    lock (sync)
                    {
                        active--;
                    }
                }
            }
        };
        var matcher = new StubMatcher { QueryFactory = context => $"query-{context.DeckId}" };
        var service = CreateService(client, matcher);

        await Task.WhenAll(Enumerable.Range(1, 8).Select(id =>
            service.ResolveCoverAsync(CreateContext(id))));

        Assert.Equal(8, client.SearchCalls);
        Assert.InRange(maximumActive, 1, 3);
    }

    [Fact]
    public async Task VerifyVolumeCoverAsync_InferredProof_SucceedsWhenFreshSearchAndFetchReproduceProof()
    {
        var volume = new GoogleBooksVolumeDto(
            "d0Qj0AEACAAJ",
            new GoogleBooksVolumeInfoDto(
                Title: "Re:ゼロから始める異世界生活",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040691435")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [volume])),
            VolumeResult = GoogleBooksClientResult<GoogleBooksVolumeDto>.Succeeded(volume)
        };
        var realMatcher = new GoogleBooksCoverMatcher();
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, ValidatedUrl: "https://books.google.com/cover.jpg", Width: 128, Height: 192, IsLowResolution: true)
        };
        var service = CreateService(client, realMatcher, validator);

        var result = await service.VerifyVolumeCoverAsync("d0Qj0AEACAAJ", CreateReZeroContext());

        Assert.Equal(GoogleBooksMatchStatus.Matched, result.Status);
        Assert.Equal("d0Qj0AEACAAJ", result.VolumeId);
        Assert.Equal(GoogleBooksIdentityProof.CrossQueryInferredVolume, result.Proof);
        Assert.True(result.IsLowResolution);
        Assert.Contains(result.Evidence!, e => e.Contains("volume-12 searches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyVolumeCoverAsync_InferredProof_RejectsWhenFreshFetchedIsbnChanged()
    {
        var searchVolume = new GoogleBooksVolumeDto(
            "d0Qj0AEACAAJ",
            new GoogleBooksVolumeInfoDto(
                Title: "Re:ゼロから始める異世界生活",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040691435")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var changedVolume = new GoogleBooksVolumeDto(
            "d0Qj0AEACAAJ",
            new GoogleBooksVolumeInfoDto(
                Title: "Re:ゼロから始める異世界生活",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040734804")], // different ISBN
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [searchVolume])),
            VolumeResult = GoogleBooksClientResult<GoogleBooksVolumeDto>.Succeeded(changedVolume)
        };
        var realMatcher = new GoogleBooksCoverMatcher();
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, ValidatedUrl: "https://books.google.com/cover.jpg", Width: 128, Height: 192, IsLowResolution: true)
        };
        var service = CreateService(client, realMatcher, validator);

        var result = await service.VerifyVolumeCoverAsync("d0Qj0AEACAAJ", CreateReZeroContext());

        Assert.Equal(GoogleBooksMatchStatus.NoMatch, result.Status);
        Assert.Contains("ISBN did not match", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyVolumeCoverAsync_InferredProof_RejectsWhenFreshFetchedLanguageChanged()
    {
        var searchVolume = new GoogleBooksVolumeDto(
            "d0Qj0AEACAAJ",
            new GoogleBooksVolumeInfoDto(
                Title: "Re:ゼロから始める異世界生活",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040691435")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var changedVolume = new GoogleBooksVolumeDto(
            "d0Qj0AEACAAJ",
            new GoogleBooksVolumeInfoDto(
                Title: "Re:ゼロから始める異世界生活",
                Language: "en", // changed to English
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040691435")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [searchVolume])),
            VolumeResult = GoogleBooksClientResult<GoogleBooksVolumeDto>.Succeeded(changedVolume)
        };
        var realMatcher = new GoogleBooksCoverMatcher();
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, ValidatedUrl: "https://books.google.com/cover.jpg", Width: 128, Height: 192, IsLowResolution: true)
        };
        var service = CreateService(client, realMatcher, validator);

        var result = await service.VerifyVolumeCoverAsync("d0Qj0AEACAAJ", CreateReZeroContext());

        Assert.Equal(GoogleBooksMatchStatus.NoMatch, result.Status);
        Assert.Contains("language is not Japanese", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyVolumeCoverAsync_ExplicitVolume_SucceedsWhenSearchAndFetchReproduceProof()
    {
        var volume = new GoogleBooksVolumeDto(
            "spy_1",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040734804")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/cover.jpg")));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [volume])),
            VolumeResult = GoogleBooksClientResult<GoogleBooksVolumeDto>.Succeeded(volume)
        };
        var realMatcher = new GoogleBooksCoverMatcher();
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, ValidatedUrl: "https://books.google.com/cover.jpg", Width: 300, Height: 450, IsLowResolution: false)
        };
        var service = CreateService(client, realMatcher, validator);

        var result = await service.VerifyVolumeCoverAsync("spy_1", CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Matched, result.Status);
        Assert.Equal("spy_1", result.VolumeId);
        Assert.Equal(GoogleBooksIdentityProof.ExplicitVolume, result.Proof);
        Assert.False(result.IsLowResolution);
    }

    [Fact]
    public async Task ResolveCoverAsync_WhenGoogleHasLowResAndOpenLibraryHasStandard_UpgradesToOpenLibrary()
    {
        var volume = new GoogleBooksVolumeDto(
            "vol_1",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto("https://books.google.com/thumb.jpg"),
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784048933414")]));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [volume]))
        };
        var matcher = new StubMatcher
        {
            MatchedVolume = new GoogleBooksMatchedVolume(
                volume,
                GoogleBooksIdentityProof.ExplicitVolume,
                ["Stub match"],
                "9784048933414")
        };
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, "https://books.google.com/thumb.jpg", Width: 128, Height: 180, IsLowResolution: true)
        };
        var olClient = new StubOpenLibraryClient
        {
            Handler = isbn => Task.FromResult(OpenLibraryCoverResult.Matched(
                isbn,
                $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false",
                800,
                1200,
                false,
                []))
        };

        var service = CreateService(client, matcher, validator, olClient);
        var result = await service.ResolveCoverAsync(CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Matched, result.Status);
        Assert.Equal(ExternalCoverProvider.OpenLibrary, result.Provider);
        Assert.Equal("9784048933414", result.VolumeId);
        Assert.Contains("covers.openlibrary.org", result.CoverUrl);
        Assert.False(result.IsLowResolution);
    }

    [Fact]
    public async Task ResolveCoverAsync_WhenOpenLibraryImageIsNotMateriallyBetter_RetainsGoogleBooksCover()
    {
        var volume = new GoogleBooksVolumeDto(
            "vol_1",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto("https://books.google.com/thumb.jpg"),
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784048933414")]));

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(1, [volume]))
        };
        var matcher = new StubMatcher
        {
            MatchedVolume = new GoogleBooksMatchedVolume(
                volume,
                GoogleBooksIdentityProof.ExplicitVolume,
                ["Stub match"],
                "9784048933414")
        };
        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, "https://books.google.com/thumb.jpg", Width: 600, Height: 900, IsLowResolution: false)
        };
        var olClient = new StubOpenLibraryClient
        {
            Handler = isbn => Task.FromResult(OpenLibraryCoverResult.Matched(
                isbn,
                $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false",
                620,
                920,
                false,
                []))
        };

        var service = CreateService(client, matcher, validator, olClient);
        var result = await service.ResolveCoverAsync(CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Matched, result.Status);
        Assert.Equal(ExternalCoverProvider.GoogleBooks, result.Provider);
        Assert.Equal("vol_1", result.VolumeId);
        Assert.Equal("https://books.google.com/thumb.jpg", result.CoverUrl);
    }

    [Fact]
    public async Task ResolveCoverAsync_AmbiguousEditions_PopulatesAtMostThreeEditionOptionsSortedByResolution()
    {
        var editions = Enumerable.Range(1, 5).Select(i => new GoogleBooksVolumeDto(
            $"vol_{i}",
            new GoogleBooksVolumeInfoDto(
                Title: $"Edition {i}",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto($"https://books.google.com/vol_{i}.jpg"),
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", $"978404893341{i}")]))).ToList();

        var client = new StubClient
        {
            SearchResult = GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(5, editions))
        };

        var matcher = new StubMatcher
        {
            FindUniqueMatchingOverride = (GoogleCoverLookupContext ctx, IReadOnlyList<GoogleBooksQueryResultGroup> groups, out GoogleBooksCoverMatchResult? failure) =>
            {
                failure = GoogleBooksCoverMatchResult.CreateAmbiguous(
                    candidateEditions: editions,
                    evidence: ["Multiple exact editions found"]);
                return null;
            }
        };

        var validator = new StubImageValidator
        {
            Handler = () => new ImageValidationResult(true, "https://books.google.com/cover.jpg", Width: 400, Height: 600)
        };

        var service = CreateService(client, matcher, validator);
        var result = await service.ResolveCoverAsync(CreateContext());

        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, result.Status);
        Assert.NotNull(result.EditionOptions);
        Assert.True(result.EditionOptions.Count <= 3);
    }

    private static GoogleBooksCoverService CreateService(
        StubClient client,
        IGoogleBooksCoverMatcher? matcher = null,
        StubImageValidator? validator = null,
        IOpenLibraryCoverClient? openLibraryClient = null) =>
        new(
            client,
            matcher ?? new StubMatcher(),
            validator ?? new StubImageValidator(),
            NullLogger<GoogleBooksCoverService>.Instance,
            openLibraryClient);

    private static GoogleCoverLookupContext CreateContext(int deckId = 10) =>
        new(
            "スパイ教室 1",
            new ParsedMediaTitle
            {
                OriginalTitle = "スパイ教室 1",
                BaseTitle = "スパイ教室",
                ComparisonTitle = "スパイ教室 1",
                Volume = StructuredVolume.Standard(1)
            },
            StructuredVolume.Standard(1),
            "スパイ教室",
            null,
            null,
            "第1巻",
            StructuredVolume.Standard(1),
            null,
            null,
            null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: deckId,
            SubdeckId: 11,
            IsExactIdentity: true);

    private static GoogleCoverLookupContext CreateReZeroContext(bool isExactIdentity = true)
    {
        var rawTitle = "Ｒｅ：ゼロから始める異世界生活12";
        var parsed = MediaTitleParser.ParseTitle(rawTitle);
        return new GoogleCoverLookupContext(
            RawTtsuTitle: rawTitle,
            ParsedTtsuTitle: parsed,
            TargetVolume: StructuredVolume.Standard(12),
            JitenParentOriginalTitle: "Re:ゼロから始める異世界生活",
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: "第12巻",
            JitenSubdeckVolume: StructuredVolume.Standard(12),
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 10,
            SubdeckId: 22,
            IsExactIdentity: isExactIdentity);
    }

    private sealed class StubClient : IGoogleBooksClient
    {
        public bool IsConfigured { get; init; } = true;
        public int SearchCalls { get; private set; }
        public int VolumeCalls { get; private set; }
        public Func<string, CancellationToken, Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>>>? SearchHandler { get; init; }
        public GoogleBooksClientResult<GoogleBooksSearchResponseDto> SearchResult { get; init; } =
            GoogleBooksClientResult<GoogleBooksSearchResponseDto>.Succeeded(
                new GoogleBooksSearchResponseDto(0, []));
        public GoogleBooksClientResult<GoogleBooksVolumeDto> VolumeResult { get; init; } =
            GoogleBooksClientResult<GoogleBooksVolumeDto>.Failed(GoogleBooksClientStatus.NotFound);

        public Task<GoogleBooksClientResult<GoogleBooksSearchResponseDto>> SearchVolumesAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return SearchHandler?.Invoke(query, cancellationToken) ?? Task.FromResult(SearchResult);
        }

        public Task<GoogleBooksClientResult<GoogleBooksVolumeDto>> GetVolumeAsync(
            string volumeId,
            CancellationToken cancellationToken = default)
        {
            VolumeCalls++;
            return Task.FromResult(VolumeResult);
        }
    }

    private sealed class StubMatcher : IGoogleBooksCoverMatcher
    {
        public delegate GoogleBooksMatchedVolume? FindUniqueMatchingVolumeHandler(
            GoogleCoverLookupContext context,
            IReadOnlyList<GoogleBooksQueryResultGroup> queryGroups,
            out GoogleBooksCoverMatchResult? failureResult);

        public FindUniqueMatchingVolumeHandler? FindUniqueMatchingOverride { get; init; }

        public GoogleBooksVolumeDto? Match { get; init; }
        public Func<GoogleCoverLookupContext, string>? QueryFactory { get; init; }

        public GoogleBooksMatchedVolume? MatchedVolume { get; init; }

        public IReadOnlyList<string> GenerateQueries(GoogleCoverLookupContext context) =>
            [QueryFactory?.Invoke(context) ?? "query"];

        public GoogleBooksMatchedVolume? FindUniqueMatchingVolume(
            GoogleCoverLookupContext context,
            IReadOnlyList<GoogleBooksQueryResultGroup> queryGroups,
            out GoogleBooksCoverMatchResult? failureResult)
        {
            if (FindUniqueMatchingOverride is not null)
            {
                return FindUniqueMatchingOverride(context, queryGroups, out failureResult);
            }

            if (MatchedVolume is not null)
            {
                failureResult = null;
                return MatchedVolume;
            }

            if (Match is not null)
            {
                failureResult = null;
                return new GoogleBooksMatchedVolume(
                    Match,
                    GoogleBooksIdentityProof.ExplicitVolume,
                    ["Stub match"]);
            }

            var first = queryGroups.SelectMany(g => g.Items).FirstOrDefault();
            if (first is not null)
            {
                failureResult = null;
                return new GoogleBooksMatchedVolume(
                    first,
                    GoogleBooksIdentityProof.ExplicitVolume,
                    ["Stub match"]);
            }

            failureResult = GoogleBooksCoverMatchResult.CreateNoMatch();
            return null;
        }

        public GoogleBooksVolumeDto? FindUniqueMatchingVolume(
            GoogleCoverLookupContext context,
            IEnumerable<GoogleBooksVolumeDto> candidates,
            out GoogleBooksCoverMatchResult? failureResult)
        {
            failureResult = Match is null ? GoogleBooksCoverMatchResult.CreateNoMatch() : null;
            return Match;
        }
    }

    private sealed class StubImageValidator : IGoogleBooksImageValidator
    {
        public Func<ImageValidationResult>? Handler { get; init; }

        public Task<ImageValidationResult> ValidateCoverImageAsync(
            string? rawImageUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Handler?.Invoke() ?? new ImageValidationResult(false, FailureReason: "Not expected."));
    }

    private sealed class StubOpenLibraryClient : IOpenLibraryCoverClient
    {
        public Func<string, Task<OpenLibraryCoverResult>>? Handler { get; init; }
        public Task<OpenLibraryCoverResult> GetCoverByIsbnAsync(string rawIsbn, CancellationToken cancellationToken = default) =>
            Handler?.Invoke(rawIsbn) ?? Task.FromResult(OpenLibraryCoverResult.NoMatch());
    }
}

