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
}
