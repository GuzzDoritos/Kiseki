using Kiseki.Core.Models.GoogleBooks;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services.GoogleBooks;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class GoogleBooksMatcherTests
{
    private readonly GoogleBooksCoverMatcher _matcher = new();

    [Fact]
    public void GenerateQueries_StandardNumberedVolume_ProducesAtMostTwoVolumeQueries_NeverSeriesOnly()
    {
        var parsed = MediaTitleParser.ParseTitle("無職転生 1");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "無職転生 1",
            ParsedTtsuTitle: parsed,
            TargetVolume: parsed.Volume,
            JitenParentOriginalTitle: "無職転生 〜異世界行ったら本気だす〜",
            JitenParentRomajiTitle: "Mushoku Tensei: Isekai Ittara Honki Dasu",
            JitenParentEnglishTitle: "Jobless Reincarnation",
            JitenSubdeckTitle: "第1巻",
            JitenSubdeckVolume: StructuredVolume.Standard(1),
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 100,
            SubdeckId: 101);

        var queries = _matcher.GenerateQueries(context);

        Assert.Equal(2, queries.Count);
        Assert.NotEqual(queries[0], queries[1]);
        Assert.All(queries, q =>
        {
            Assert.Contains("1", q);
            Assert.NotEqual("無職転生 〜異世界行ったら本気だす〜", q); // Never series-only
        });
        Assert.Contains("無職転生 〜異世界行ったら本気だす〜 1巻", queries);
        Assert.Contains("intitle:無職転生 〜異世界行ったら本気だす〜 1", queries);
    }

    [Fact]
    public void GenerateQueries_StandaloneVolume_ProducesSingleExactTitleQuery()
    {
        var parsed = MediaTitleParser.ParseTitle("君の名は。");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "君の名は。",
            ParsedTtsuTitle: parsed,
            TargetVolume: null,
            JitenParentOriginalTitle: null,
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: "君の名は。",
            JitenDeckRomajiTitle: "Kimi no Na wa.",
            JitenDeckEnglishTitle: "Your Name.",
            IsSubdeck: false,
            IsStandalone: true,
            DeckId: 200,
            SubdeckId: null);

        var queries = _matcher.GenerateQueries(context);

        Assert.Single(queries);
        Assert.Equal("君の名は。", queries[0]);
    }

    [Fact]
    public void GenerateQueries_NumberedStandaloneDeck_DoesNotDuplicateVolumeMarker()
    {
        var parsed = MediaTitleParser.ParseTitle("本好きの下剋上 1");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "本好きの下剋上 1",
            ParsedTtsuTitle: parsed,
            TargetVolume: parsed.Volume,
            JitenParentOriginalTitle: null,
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: "本好きの下剋上 1",
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: false,
            IsStandalone: true,
            DeckId: 201,
            SubdeckId: null);

        var queries = _matcher.GenerateQueries(context);

        Assert.Equal(2, queries.Count);
        Assert.NotEqual(queries[0], queries[1]);
        Assert.Contains("本好きの下剋上 1巻", queries);
        Assert.Contains("intitle:本好きの下剋上 1", queries);
        Assert.DoesNotContain(queries, query => query.Contains("1 1", StringComparison.Ordinal));
    }

    [Fact]
    public void FindUniqueMatchingVolume_NumberedStandaloneDeck_MatchesJitenBaseTitleWithoutMarker()
    {
        var parsed = MediaTitleParser.ParseTitle("Ascendance of a Bookworm 1");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "Ascendance of a Bookworm 1",
            ParsedTtsuTitle: parsed,
            TargetVolume: parsed.Volume,
            JitenParentOriginalTitle: null,
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: "本好きの下剋上 1",
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: false,
            IsStandalone: true,
            DeckId: 202,
            SubdeckId: null);
        var candidate = CreateVolume(
            "bookworm_1",
            "本好きの下剋上 1",
            "ja",
            "https://books.google.com/img.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Equal("bookworm_1", Assert.IsType<GoogleBooksVolumeDto>(match).Id);
        Assert.Null(failure);
    }

    [Fact]
    public void GenerateQueries_SpecialOrFractionalVolume_ProducesNoQueries()
    {
        var parsed = MediaTitleParser.ParseTitle("ようこそ実力至上主義の教室へ 11.5");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "ようこそ実力至上主義の教室へ 11.5",
            ParsedTtsuTitle: parsed,
            TargetVolume: parsed.Volume,
            JitenParentOriginalTitle: "ようこそ実力至上主義の教室へ",
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 300,
            SubdeckId: 301);

        var queries = _matcher.GenerateQueries(context);

        Assert.Empty(queries);
    }

    [Fact]
    public void FindUniqueMatchingVolume_ExactJapaneseMatch_ReturnsCandidate()
    {
        var parsed = MediaTitleParser.ParseTitle("スパイ教室 01");
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);

        var candidate = CreateVolume("vol_123", "スパイ教室 1", "ja", "https://books.google.com/img1.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.NotNull(match);
        Assert.Null(failure);
        Assert.Equal("vol_123", match.Id);
    }

    [Theory]
    [InlineData("スパイ教室 新章 1")]
    [InlineData("スパイ 1")]
    [InlineData("スパイ教室")]
    public void FindUniqueMatchingVolume_RejectsContainmentNearMatchAndMissingVolume(string candidateTitle)
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var candidate = CreateVolume(
            "near_match",
            candidateTitle,
            "ja",
            "https://books.google.com/img1.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_StandaloneRequiresExactTitle()
    {
        var parsed = MediaTitleParser.ParseTitle("君の名は。");
        var context = new GoogleCoverLookupContext(
            "君の名は。",
            parsed,
            TargetVolume: null,
            JitenParentOriginalTitle: null,
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: "君の名は。",
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: false,
            IsStandalone: true,
            DeckId: 200,
            SubdeckId: null);
        var exact = CreateVolume("exact", "君の名は。", "ja", "https://books.google.com/exact.jpg");
        var near = CreateVolume("near", "君の名は。 Another Side", "ja", "https://books.google.com/near.jpg");

        var exactMatch = _matcher.FindUniqueMatchingVolume(context, [near, exact], out var exactFailure);
        var nearMatch = _matcher.FindUniqueMatchingVolume(context, [near], out var nearFailure);

        Assert.Equal("exact", Assert.IsType<GoogleBooksVolumeDto>(exactMatch).Id);
        Assert.Null(exactFailure);
        Assert.Null(nearMatch);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, Assert.IsType<GoogleBooksCoverMatchResult>(nearFailure).Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_ResultOrderAndSupportingEvidenceCannotBypassHardGates()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var eligible = CreateVolume("eligible", "スパイ教室 1", "ja", "https://books.google.com/ok.jpg");
        var ineligible = new GoogleBooksVolumeDto(
            "ineligible",
            new GoogleBooksVolumeInfoDto(
                Title: "Different Series 1",
                Authors: ["Trusted Author"],
                PublishedDate: "2026-01-01",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9780000000000")],
                ImageLinks: new GoogleBooksImageLinksDto(
                    Thumbnail: "https://books.google.com/no.jpg")));

        var firstOrder = _matcher.FindUniqueMatchingVolume(context, [ineligible, eligible], out var firstFailure);
        var secondOrder = _matcher.FindUniqueMatchingVolume(context, [eligible, ineligible], out var secondFailure);

        Assert.Equal("eligible", Assert.IsType<GoogleBooksVolumeDto>(firstOrder).Id);
        Assert.Equal("eligible", Assert.IsType<GoogleBooksVolumeDto>(secondOrder).Id);
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsLocalizedEnglishCandidate()
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var candidate = CreateVolume("vol_en", "Spy Classroom 1", "en", "https://books.google.com/img1.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsMissingOrNonJapaneseLanguage()
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var candidate1 = CreateVolume("vol_null", "スパイ教室 1", null, "https://books.google.com/img1.jpg");
        var candidate2 = CreateVolume("vol_fr", "スパイ教室 1", "fr", "https://books.google.com/img1.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate1, candidate2], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsCandidateWithoutImage()
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var candidate = new GoogleBooksVolumeDto("no_img", new GoogleBooksVolumeInfoDto("スパイ教室 1", Language: "ja"));

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_VolumeMismatch_RejectsAdjacentVolume()
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var candVol2 = CreateVolume("vol_2", "スパイ教室 2", "ja", "https://books.google.com/img.jpg");
        var candVol10 = CreateVolume("vol_10", "スパイ教室 10", "ja", "https://books.google.com/img.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candVol2, candVol10], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsOmnibusOrCombinedEditions()
    {
        var context = CreateContext("無職転生 1", "無職転生", 1);
        var candidate = CreateVolume("vol_omni", "無職転生 1-3 合本版", "ja", "https://books.google.com/img.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsMangaAdaptationWhenBaseTitleDiffers()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var candidate = CreateVolume("vol_manga", "スパイ教室 コミック 1", "ja", "https://books.google.com/img.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_TwoEligibleCandidates_ReturnsAmbiguous()
    {
        var context = CreateContext("スパイ教室 01", "スパイ教室", 1);
        var cand1 = CreateVolume("id_1", "スパイ教室 1", "ja", "https://books.google.com/cover1.jpg");
        var cand2 = CreateVolume("id_2", "スパイ教室 1", "ja", "https://books.google.com/cover2.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [cand1, cand2], out var failure);

        Assert.Null(match);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_NormalizationHandlesNFKCAndPunctuation()
    {
        var parsed = MediaTitleParser.ParseTitle("Ｒｅ：ゼロから始める異世界生活 １");
        var context = new GoogleCoverLookupContext(
            RawTtsuTitle: "Ｒｅ：ゼロから始める異世界生活 １",
            ParsedTtsuTitle: parsed,
            TargetVolume: StructuredVolume.Standard(1),
            JitenParentOriginalTitle: "Re:ゼロから始める異世界生活",
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 1,
            SubdeckId: 2);

        var candidate = CreateVolume("rezero_1", "Re：ゼロから始める異世界生活 1", "ja", "https://books.google.com/c.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.NotNull(match);
        Assert.Null(failure);
        Assert.Equal("rezero_1", match.Id);
    }

    [Fact]
    public void GenerateQueries_SubdeckWithoutExplicitVolume_DoesNotIssueSeriesOnlyQuery()
    {
        var parsed = MediaTitleParser.ParseTitle("スパイ教室");
        var context = new GoogleCoverLookupContext(
            "スパイ教室",
            parsed,
            TargetVolume: null,
            JitenParentOriginalTitle: "スパイ教室",
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: "Unknown part",
            JitenSubdeckVolume: null,
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 100,
            SubdeckId: 101);

        Assert.Empty(_matcher.GenerateQueries(context));

        var candidate = CreateVolume(
            "series_record",
            "スパイ教室",
            "ja",
            "https://books.google.com/img.jpg");
        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_RejectsConflictingTitleAndSubtitleVolumes()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var candidate = new GoogleBooksVolumeDto(
            "conflicting_record",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Subtitle: "第2巻",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto(
                    Thumbnail: "https://books.google.com/img.jpg")));

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    [Theory]
    [InlineData("日本語")]
    [InlineData("has/slash")]
    public void FindUniqueMatchingVolume_RejectsUnsafeProviderVolumeId(string volumeId)
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var candidate = CreateVolume(
            volumeId,
            "スパイ教室 1",
            "ja",
            "https://books.google.com/img.jpg");

        var match = _matcher.FindUniqueMatchingVolume(context, [candidate], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    private static GoogleCoverLookupContext CreateContext(string ttsuTitle, string parentTitle, int volumeNumber)
    {
        var parsed = MediaTitleParser.ParseTitle(ttsuTitle);
        return new GoogleCoverLookupContext(
            RawTtsuTitle: ttsuTitle,
            ParsedTtsuTitle: parsed,
            TargetVolume: StructuredVolume.Standard(volumeNumber),
            JitenParentOriginalTitle: parentTitle,
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: null,
            JitenSubdeckVolume: StructuredVolume.Standard(volumeNumber),
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 100,
            SubdeckId: 101);
    }

    private static GoogleBooksVolumeDto CreateVolume(string id, string title, string? language, string imageUrl)
    {
        return new GoogleBooksVolumeDto(
            id,
            new GoogleBooksVolumeInfoDto(
                Title: title,
                Language: language,
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: imageUrl)));
    }

    [Fact]
    public void FindUniqueMatchingVolume_EquivalentApiRecordsSharingNormalizedIsbn_CollapsesSafely()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var rec1 = new GoogleBooksVolumeDto(
            "vol1_thumb",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040734804")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/books/content?id=abc12345&zoom=1")));

        var rec2 = new GoogleBooksVolumeDto(
            "vol1_small",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040734804")],
                ImageLinks: new GoogleBooksImageLinksDto(Small: "https://books.google.com/books/content?id=abc12345&zoom=2")));

        var match = _matcher.FindUniqueMatchingVolume(context, [rec1, rec2], out var failure);

        Assert.NotNull(match);
        Assert.Null(failure);
        Assert.Equal("vol1_small", match.Id);
    }

    [Fact]
    public void FindUniqueMatchingVolume_DifferentIsbnGroups_RemainsAmbiguous()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var rec1 = new GoogleBooksVolumeDto(
            "vol1_editionA",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040734804")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/books/content?id=abc12345&zoom=1")));

        var rec2 = new GoogleBooksVolumeDto(
            "vol1_editionB",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: [new GoogleBooksIndustryIdentifierDto("ISBN_13", "9784040799999")],
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/books/content?id=xyz98765&zoom=1")));

        var match = _matcher.FindUniqueMatchingVolume(context, [rec1, rec2], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_TitleOnlyDuplicatesWithoutIdentifiers_NotAssumedEquivalent_RemainsAmbiguous()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var rec1 = new GoogleBooksVolumeDto(
            "vol1_no_id_A",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: null,
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/books/content?id=abc12345&zoom=1")));

        var rec2 = new GoogleBooksVolumeDto(
            "vol1_no_id_B",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                IndustryIdentifiers: null,
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: "https://books.google.com/books/content?id=abc12345&zoom=1")));

        var match = _matcher.FindUniqueMatchingVolume(context, [rec1, rec2], out var failure);

        Assert.Null(match);
        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, Assert.IsType<GoogleBooksCoverMatchResult>(failure).Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_PrefersLargestSuppliedImageLink()
    {
        var context = CreateContext("スパイ教室 1", "スパイ教室", 1);
        var rec = new GoogleBooksVolumeDto(
            "vol1_multi_images",
            new GoogleBooksVolumeInfoDto(
                Title: "スパイ教室 1",
                Language: "ja",
                ImageLinks: new GoogleBooksImageLinksDto(
                    Thumbnail: "https://books.google.com/thumb.jpg",
                    Small: "https://books.google.com/small.jpg",
                    Medium: "https://books.google.com/medium.jpg",
                    Large: "https://books.google.com/large.jpg")));

        var match = _matcher.FindUniqueMatchingVolume(context, [rec], out var failure);

        Assert.NotNull(match);
        Assert.Null(failure);
        Assert.Equal("https://books.google.com/large.jpg", match.VolumeInfo?.ImageLinks?.GetPreferredImageLink());
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_MatchesReZeroVolume12Fixture_AcrossBothQueryGroups()
    {
        var context = CreateReZeroContext(12);
        var vol = CreateMarkerlessVolume();
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [vol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.NotNull(matched);
        Assert.Null(failure);
        Assert.Equal("d0Qj0AEACAAJ", matched.Volume.Id);
        Assert.Equal(GoogleBooksIdentityProof.CrossQueryInferredVolume, matched.Proof);
        Assert.Equal("9784040691435", matched.NormalizedIsbn);
        Assert.Contains(matched.Evidence, e => e.Contains("volume-12 searches", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RejectsCandidateAppearingInOnlyOneGroup()
    {
        var context = CreateReZeroContext(12);
        var vol = CreateMarkerlessVolume();
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", []);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
        Assert.Contains("cross-query proof was insufficient", failure.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RejectsCandidateWithDifferentIsbnAcrossGroups()
    {
        var context = CreateReZeroContext(12);
        var vol1 = CreateMarkerlessVolume(isbn13: "9784040691435");
        var vol2 = CreateMarkerlessVolume(isbn13: "9784040734804");
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol1]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [vol2]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RejectsInvalidIsbnCheckDigit()
    {
        var context = CreateReZeroContext(12);
        var vol = CreateMarkerlessVolume(isbn13: "9784040691439"); // invalid check digit (expected 5, got 9)
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [vol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_TwoDistinctValidIsbnGroups_ReturnsAmbiguous()
    {
        var context = CreateReZeroContext(12);
        var vol1 = CreateMarkerlessVolume("vol_a", isbn13: "9784040691435");
        var vol2 = CreateMarkerlessVolume("vol_b", isbn13: "9784040734804");
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol1, vol2]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [vol1, vol2]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, failure.Status);
        Assert.Contains("multiple markerless editions remained ambiguous", failure.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindUniqueMatchingVolume_StrictExplicitVolume_TakesPrecedenceOverInferred()
    {
        var context = CreateReZeroContext(12);
        var strictVol = CreateVolume("strict_12", "Re:ゼロから始める異世界生活 12", "ja", "https://books.google.com/cover.jpg");
        var markerlessVol = CreateMarkerlessVolume();
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [markerlessVol, strictVol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [markerlessVol, strictVol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.NotNull(matched);
        Assert.Null(failure);
        Assert.Equal("strict_12", matched.Volume.Id);
        Assert.Equal(GoogleBooksIdentityProof.ExplicitVolume, matched.Proof);
    }

    [Fact]
    public void FindUniqueMatchingVolume_StrictAmbiguity_CannotBeBypassedByMarkerless()
    {
        var context = CreateReZeroContext(12);
        var strictVol1 = CreateVolume("strict_1", "Re:ゼロから始める異世界生活 12", "ja", "https://books.google.com/cover1.jpg");
        var strictVol2 = CreateVolume("strict_2", "Re:ゼロから始める異世界生活 12", "ja", "https://books.google.com/cover2.jpg");
        var markerlessVol = CreateMarkerlessVolume();
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [strictVol1, strictVol2, markerlessVol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [strictVol1, strictVol2, markerlessVol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.Ambiguous, failure.Status);
    }

    [Theory]
    [InlineData("Re:ゼロから始める異世界生活 短編集 12")]
    [InlineData("Re:ゼロから始める異世界生活 12 コミックス")]
    [InlineData("Re:ゼロから始める異世界生活 漫画 12")]
    [InlineData("Re:ゼロから始める異世界生活 12-14 合本版")]
    [InlineData("Re:ゼロから始める異世界生活 12 (上)")]
    [InlineData("Re:ゼロから始める異世界生活 12巻 前編")]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RejectsNoiseVariantsEvenWithTargetNumber(string noisyTitle)
    {
        var context = CreateReZeroContext(12);
        var noisyVol = CreateMarkerlessVolume(title: noisyTitle);
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [noisyVol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [noisyVol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RejectsNonJapaneseLanguages(string lang)
    {
        var context = CreateReZeroContext(12);
        var vol = CreateMarkerlessVolume(language: lang);
        var group1 = new GoogleBooksQueryResultGroup("Re:ゼロから始める異世界生活 12巻", [vol]);
        var group2 = new GoogleBooksQueryResultGroup("intitle:Re:ゼロから始める異世界生活 12", [vol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_ProviderOrderDoesNotChangeResult()
    {
        var context = CreateReZeroContext(12);
        var markerless = CreateMarkerlessVolume();
        var other = CreateMarkerlessVolume("other_vol", title: "Unrelated Book", isbn13: "9784040734804");

        var group1A = new GoogleBooksQueryResultGroup("q1", [markerless, other]);
        var group2A = new GoogleBooksQueryResultGroup("q2", [other, markerless]);
        var matchA = _matcher.FindUniqueMatchingVolume(context, [group1A, group2A], out var failureA);

        var group1B = new GoogleBooksQueryResultGroup("q1", [other, markerless]);
        var group2B = new GoogleBooksQueryResultGroup("q2", [markerless, other]);
        var matchB = _matcher.FindUniqueMatchingVolume(context, [group1B, group2B], out var failureB);

        Assert.NotNull(matchA);
        Assert.NotNull(matchB);
        Assert.Equal("d0Qj0AEACAAJ", matchA.Volume.Id);
        Assert.Equal("d0Qj0AEACAAJ", matchB.Volume.Id);
        Assert.Null(failureA);
        Assert.Null(failureB);
    }

    [Fact]
    public void FindUniqueMatchingVolume_MarkerlessFallback_RequiresIsExactIdentityTrue()
    {
        var context = CreateReZeroContext(12, isExactIdentity: false);
        var vol = CreateMarkerlessVolume();
        var group1 = new GoogleBooksQueryResultGroup("q1", [vol]);
        var group2 = new GoogleBooksQueryResultGroup("q2", [vol]);

        var matched = _matcher.FindUniqueMatchingVolume(context, [group1, group2], out var failure);

        Assert.Null(matched);
        Assert.NotNull(failure);
        Assert.Equal(GoogleBooksMatchStatus.NoMatch, failure.Status);
    }

    private static GoogleCoverLookupContext CreateReZeroContext(int volume = 12, bool isExactIdentity = true)
    {
        var rawTitle = $"Ｒｅ：ゼロから始める異世界生活{volume}";
        var parsed = MediaTitleParser.ParseTitle(rawTitle);
        return new GoogleCoverLookupContext(
            RawTtsuTitle: rawTitle,
            ParsedTtsuTitle: parsed,
            TargetVolume: StructuredVolume.Standard(volume),
            JitenParentOriginalTitle: "Re:ゼロから始める異世界生活",
            JitenParentRomajiTitle: null,
            JitenParentEnglishTitle: null,
            JitenSubdeckTitle: $"第{volume}巻",
            JitenSubdeckVolume: StructuredVolume.Standard(volume),
            JitenDeckOriginalTitle: null,
            JitenDeckRomajiTitle: null,
            JitenDeckEnglishTitle: null,
            IsSubdeck: true,
            IsStandalone: false,
            DeckId: 10,
            SubdeckId: 10 + volume,
            IsExactIdentity: isExactIdentity);
    }

    private static GoogleBooksVolumeDto CreateMarkerlessVolume(
        string id = "d0Qj0AEACAAJ",
        string title = "Re:ゼロから始める異世界生活",
        string? isbn13 = "9784040691435",
        string language = "ja",
        string imageUrl = "https://books.google.com/cover.jpg") =>
        new(
            id,
            new GoogleBooksVolumeInfoDto(
                Title: title,
                Language: language,
                IndustryIdentifiers: isbn13 is not null ? [new GoogleBooksIndustryIdentifierDto("ISBN_13", isbn13)] : null,
                ImageLinks: new GoogleBooksImageLinksDto(Thumbnail: imageUrl)));
}
