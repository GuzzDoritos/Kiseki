using System.Globalization;
using Kiseki.Core.Models.Metadata;
using Kiseki.Core.Services.Metadata;

namespace Kiseki.Tests;

public sealed class MediaTitleParserTests
{
    private readonly MediaTitleParser _parser = new();

    [Theory]
    [InlineData("【電撃文庫】ソードアート・オンライン　０１.epub", "ソードアート・オンライン", 1)]
    [InlineData("　[Light Novel]　８６―エイティシックス―　02.html　", "86―エイティシックス―", 2)]
    public void Parse_NfkcFullWidthNormalizationAndWhitespaceCollapse(
        string rawTitle,
        string expectedBaseTitle,
        int expectedVolume)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(expectedBaseTitle, result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(expectedVolume, result.Volume.Number);
        Assert.False(result.Volume.IsSpecial);
    }

    [Theory]
    [InlineData("[Light Novel] Title 1.epub", "Title", 1)]
    [InlineData("[Novel] Title 2.txt", "Title", 2)]
    [InlineData("【角川スニーカー文庫】Title 3.html", "Title", 3)]
    [InlineData("[MF文庫J][Book] Title 4", "Title", 4)]
    public void Parse_LeadingRecognizedTagsAndSuffixesRemoved(
        string rawTitle,
        string expectedBaseTitle,
        int expectedVolume)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(expectedBaseTitle, result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(expectedVolume, result.Volume.Number);
    }

    [Fact]
    public void Parse_DoesNotRemoveArbitraryBracketedSubtitles()
    {
        var result = _parser.Parse("Re:Zero (Starting Life in Another World) 1");

        Assert.Equal("Re:Zero (Starting Life in Another World)", result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(1, result.Volume.Number);
    }

    [Theory]
    [InlineData("Spice and Wolf 1", 1)]
    [InlineData("Spice and Wolf 01", 1)]
    [InlineData("Spice and Wolf (01)", 1)]
    [InlineData("Spice and Wolf Vol. 1", 1)]
    [InlineData("Spice and Wolf 第1巻", 1)]
    [InlineData("Spice and Wolf 1巻", 1)]
    public void Parse_ArabicVolumeEquivalence(string rawTitle, int expectedNumber)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal("Spice and Wolf", result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(VolumeKind.Standard, result.Volume.Kind);
        Assert.Equal(expectedNumber, result.Volume.Number);
        Assert.False(result.Volume.IsSpecial);
    }

    [Theory]
    [InlineData("Overlord Vol. IV", 4)]
    [InlineData("Overlord Volume II", 2)]
    [InlineData("Overlord Vol. IX", 9)]
    [InlineData("Overlord Vol XIV", 14)]
    public void Parse_ExplicitRomanNumeralVolumeExtracted(string rawTitle, int expectedNumber)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal("Overlord", result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(VolumeKind.Standard, result.Volume.Kind);
        Assert.Equal(expectedNumber, result.Volume.Number);
        Assert.False(result.Volume.IsSpecial);
    }

    [Theory]
    [InlineData("狼と香辛料 I")]
    [InlineData("Spice and Wolf I")]
    [InlineData("Bleach V")]
    public void Parse_BareTerminalRomanNumeralRetainedInBaseTitle(string rawTitle)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(rawTitle, result.BaseTitle);
        Assert.Null(result.Volume);
        Assert.False(result.HasVolume);
    }

    [Theory]
    [InlineData("銀河英雄伝説 上", PositionMarker.Upper)]
    [InlineData("銀河英雄伝説 (中)", PositionMarker.Middle)]
    [InlineData("銀河英雄伝説（下）", PositionMarker.Lower)]
    public void Parse_JapanesePositionMarkers(string rawTitle, PositionMarker expectedPosition)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal("銀河英雄伝説", result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(VolumeKind.Position, result.Volume.Kind);
        Assert.Equal(expectedPosition, result.Volume.Position);
        Assert.False(result.Volume.IsSpecial);
    }

    [Theory]
    [InlineData("Re:ゼロから始める異世界生活 4.5", VolumeKind.Fractional, "4.5", null)]
    [InlineData("86―エイティシックス― Ep.1", VolumeKind.Special, "1", "Episode")]
    [InlineData("ソードアート・オンライン EX", VolumeKind.Special, null, "EX")]
    [InlineData("ソードアート・オンライン EX 1", VolumeKind.Special, "1", "EX")]
    [InlineData("スレイヤーズ 短編集 1", VolumeKind.Special, "1", "ShortStories")]
    [InlineData("ようこそ実力至上主義の教室へ 2年生編 1", VolumeKind.Special, "1", "2年生編")]
    public void Parse_SpecialAndFractionalMarkersFlaggedSpecial(
        string rawTitle,
        VolumeKind expectedKind,
        string? expectedNumber,
        string? expectedTag)
    {
        var result = _parser.Parse(rawTitle);

        Assert.NotNull(result.Volume);
        Assert.Equal(expectedKind, result.Volume.Kind);
        Assert.True(result.Volume.IsSpecial);
        Assert.True(result.IsSpecialVolume);

        if (expectedNumber is not null)
        {
            Assert.Equal(decimal.Parse(expectedNumber, CultureInfo.InvariantCulture), result.Volume.Number);
        }

        if (expectedTag is not null)
        {
            Assert.Equal(expectedTag, result.Volume.SpecialTag);
        }
    }

    [Fact]
    public void Parse_InternalNumbersRetained()
    {
        var result = _parser.Parse("SAO 2024 Chapter 1");

        Assert.Equal("SAO 2024 Chapter", result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(1, result.Volume.Number);
    }

    [Theory]
    [InlineData("86")]
    [InlineData("[Light Novel] 86")]
    [InlineData("1984")]
    public void Parse_NumericTitlesNotReducedToEmptyOrTreatedAsVolume(string rawTitle)
    {
        var result = _parser.Parse(rawTitle);

        // Removal of 86 or 1984 would leave no meaningful base title, so it must be preserved
        Assert.True(result.BaseTitle.Contains("86") || result.BaseTitle.Contains("1984"));
        Assert.Null(result.Volume);
    }

    [Fact]
    public void Parse_UnsupportedJapaneseNumFormsPreservedWithoutGuessedMarker()
    {
        var result = _parser.Parse("タイトル 巻の一");

        Assert.Equal("タイトル 巻の一", result.BaseTitle);
        Assert.Null(result.Volume);
    }

    [Fact]
    public void Parse_CultureInvariant()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            // Turkish culture has unique dotted/dotless I behavior
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            var result = _parser.Parse("TITLE VOL. IV");
            Assert.Equal("TITLE", result.BaseTitle);
            Assert.NotNull(result.Volume);
            Assert.Equal(4, result.Volume.Number);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("Some Title 99999999999999999999999999999999999999999999999999")]
    [InlineData("Some Title Vol. 99999999999999999999999999999999999999999999999999")]
    [InlineData("Some Title 第99999999999999999999999999999999999999999999999999巻")]
    [InlineData("Some Title 99999999999999999999999999999999999999999999999999.5")]
    [InlineData("Some Title Vol. 99999999999999999999999999999999999999999999999999.5")]
    [InlineData("Some Title Ep. 99999999999999999999999999999999999999999999999999")]
    [InlineData("Some Title 2年生編 99999999999999999999999999999999999999999999999999")]
    public void Parse_OversizedIntegerAndDecimalMarkersNeverThrowAndRemainUnparsed(string rawTitle)
    {
        var result = _parser.Parse(rawTitle);

        Assert.NotNull(result);
        Assert.Null(result.Volume);
        Assert.False(result.HasVolume);
        Assert.Equal(rawTitle, result.BaseTitle);
    }

    [Theory]
    [InlineData("Volume 1", 1)]
    [InlineData("Vol. 02", 2)]
    [InlineData("第3巻", 3)]
    [InlineData("4巻", 4)]
    [InlineData("5", 5)]
    public void ParseVolumeMarker_AcceptsMarkerOnlyProviderFields(string marker, int expected)
    {
        var volume = MediaTitleParser.ParseVolumeMarker(marker);

        Assert.NotNull(volume);
        Assert.Equal(VolumeKind.Standard, volume.Kind);
        Assert.Equal(expected, volume.Number);
    }

    [Theory]
    [InlineData("A complete title")]
    [InlineData("")]
    public void ParseVolumeMarker_RejectsNonMarkerValues(string value)
    {
        Assert.Null(MediaTitleParser.ParseVolumeMarker(value));
    }

    [Theory]
    [InlineData("Ｒｅ：ゼロから始める異世界生活１", "Re:ゼロから始める異世界生活", 1, VolumeKind.Standard)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活１２", "Re:ゼロから始める異世界生活", 12, VolumeKind.Standard)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活１．５", "Re:ゼロから始める異世界生活", 1.5, VolumeKind.Fractional)]
    public void Parse_AttachedFullWidthVolumeSuffixExtracted(
        string rawTitle,
        string expectedBaseTitle,
        decimal expectedNumber,
        VolumeKind expectedKind)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(expectedBaseTitle, result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(expectedKind, result.Volume.Kind);
        Assert.Equal(expectedNumber, result.Volume.Number);
    }

    [Theory]
    [InlineData("作品第１巻", "作品", 1)]
    [InlineData("作品１巻", "作品", 1)]
    [InlineData("作品第2巻", "作品", 2)]
    [InlineData("作品2巻", "作品", 2)]
    public void Parse_AttachedExplicitJapaneseMarkerExtracted(
        string rawTitle,
        string expectedBaseTitle,
        int expectedNumber)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(expectedBaseTitle, result.BaseTitle);
        Assert.NotNull(result.Volume);
        Assert.Equal(expectedNumber, result.Volume.Number);
    }

    [Theory]
    [InlineData("86")]
    [InlineData("1984")]
    [InlineData("Title1")]
    [InlineData("Series2")]
    public void Parse_NumericTitlesAndAttachedAsciiDigits_RetainedAsBaseTitleWithoutVolume(string rawTitle)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(rawTitle, result.BaseTitle);
        Assert.Null(result.Volume);
        Assert.False(result.HasVolume);
    }

    [Fact]
    public void Parse_OversizedAttachedFullWidthDigits_FailsClosed()
    {
        var rawTitle = "作品９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９９";
        var result = _parser.Parse(rawTitle);

        Assert.NotNull(result);
        Assert.Null(result.Volume);
        Assert.False(result.HasVolume);
    }

    [Theory]
    [InlineData("青春ブタ野郎はナイチンゲールの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はナイチンゲールの夢を見ない")]
    [InlineData("青春ブタ野郎は迷えるシンガーの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎は迷えるシンガーの夢を見ない")]
    [InlineData("青春ブタ野郎はランドセルガールの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はランドセルガールの夢を見ない")]
    [InlineData("青春ブタ野郎はおでかけシスターの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はおでかけシスターの夢を見ない")]
    [InlineData("青春ブタ野郎はハツコイ少女の夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はハツコイ少女の夢を見ない")]
    [InlineData("青春ブタ野郎はゆめみる少女の夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はゆめみる少女の夢を見ない")]
    [InlineData("青春ブタ野郎はロジカルウィッチの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はロジカルウィッチの夢を見ない")]
    [InlineData("青春ブタ野郎はシスコンアイドルの夢を見ない 『青春ブタ野郎』シリーズ (電撃文庫)", "青春ブタ野郎はシスコンアイドルの夢を見ない")]
    [InlineData("青春ブタ野郎はプチデビル後輩の夢を見ない<青春ブタ野郎はバニーガール先輩の夢を見ない> (電撃文庫)", "青春ブタ野郎はプチデビル後輩の夢を見ない")]
    [InlineData("青春ブタ野郎はバニーガール先輩の夢を見ない<青春ブタ野郎はバニーガール先輩の夢を見ない> (電撃文庫)", "青春ブタ野郎はバニーガール先輩の夢を見ない")]
    [InlineData("青春ブタ野郎はディアフレンドの夢を見ない【ドラマＣＤ音源付き】", "青春ブタ野郎はディアフレンドの夢を見ない")]
    [InlineData("青春ブタ野郎はガールフレンドの夢を見ない【ドラマＣＤ音源付き】", "青春ブタ野郎はガールフレンドの夢を見ない")]
    public void Parse_AobutaRetailerSuffixFamilies_ProducesCleanSearchAliasAndPreservesRawTitle(
        string rawTitle,
        string expectedCleanTitle)
    {
        var result = _parser.Parse(rawTitle);

        Assert.Equal(rawTitle, result.OriginalTitle);
        Assert.NotNull(result.SearchPlan);
        Assert.Contains(expectedCleanTitle, result.SearchPlan.SearchAliases);
        Assert.True(result.SearchPlan.SearchAliases.Count <= 3);
        Assert.Equal(expectedCleanTitle, result.SearchPlan.CanonicalBaseTitle);
        Assert.NotEmpty(result.SearchPlan.Transformations);
    }

    [Theory]
    [InlineData("Ｒｅ：ゼロから始める異世界生活5", "Re:ゼロから始める異世界生活", 5)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活3", "Re:ゼロから始める異世界生活", 3)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活2", "Re:ゼロから始める異世界生活", 2)]
    public void Parse_AttachedAsciiTerminalNumber_GeneratesAttachedAsciiHypothesis(
        string rawTitle,
        string expectedBaseTitle,
        int expectedTentativeVolume)
    {
        var result = _parser.Parse(rawTitle);

        Assert.NotNull(result.SearchPlan);
        Assert.Equal(VolumeInferenceKind.AttachedAsciiHypothesis, result.SearchPlan.VolumeInference);
        Assert.Equal(expectedBaseTitle, result.SearchPlan.CanonicalBaseTitle);
        Assert.NotNull(result.SearchPlan.Volume);
        Assert.Equal(expectedTentativeVolume, result.SearchPlan.Volume.Number);
        // Canonical parser does NOT assign authoritative volume
        Assert.Null(result.Volume);
    }

    [Fact]
    public void Parse_86_RemainsNumericTitleWithoutAttachedAsciiHypothesis()
    {
        var result = _parser.Parse("86");

        Assert.Null(result.Volume);
        Assert.NotNull(result.SearchPlan);
        Assert.Equal(VolumeInferenceKind.None, result.SearchPlan.VolumeInference);
        Assert.Null(result.SearchPlan.Volume);
    }

    [Theory]
    [InlineData("Re：ゼロから始める異世界生活 Ex3　剣鬼恋譚", SeriesQualifier.Ex, 3)]
    [InlineData("Re:ゼロから始める異世界生活 Ex2　剣鬼恋歌", SeriesQualifier.Ex, 2)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活Ex", SeriesQualifier.Ex, null)]
    [InlineData("Re：ゼロから始める異世界生活　短編集２", SeriesQualifier.ShortStories, 2)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活　短編集１", SeriesQualifier.ShortStories, 1)]
    [InlineData("Ｒｅ：ゼロから始める異世界生活 大塚真一郎 Art Works　Re：BOX", SeriesQualifier.ArtBook, null)]
    public void Parse_SeriesBranches_RecognizesQualifierAndVolume(
        string rawTitle,
        SeriesQualifier expectedQualifier,
        int? expectedVolume)
    {
        var result = _parser.Parse(rawTitle);

        Assert.NotNull(result.SearchPlan);
        Assert.Equal(expectedQualifier, result.SearchPlan.SeriesQualifier);
        if (expectedVolume.HasValue)
        {
            Assert.NotNull(result.SearchPlan.Volume);
            Assert.Equal(expectedVolume.Value, result.SearchPlan.Volume.Number);
        }
        else
        {
            Assert.Null(result.SearchPlan.Volume?.Number);
        }
    }

    [Fact]
    public void Parse_HtmlEntityRepresentation_DecodedSafely()
    {
        var result = _parser.Parse("Title &amp; Subtitle &lt;Vol. 1&gt;");

        Assert.NotNull(result.SearchPlan);
        Assert.Contains("Title & Subtitle", result.SearchPlan.ComparisonTitle);
    }
}
