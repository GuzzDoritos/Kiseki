using Kiseki.Core.Entities;

namespace Kiseki.Tests;

public class MediaWorkTests
{
    [Fact]
    public void LinkToJitenDeck_UsesTheWholeDeckCount_AndSetsJitenSpecificCover()
    {
        var work = new MediaWork("Aobuta volume 1");

        work.LinkToJitenDeck(
            95367,
            109_474,
            "https://cdn.jiten.moe/aobuta.jpg");

        Assert.Equal(95367, work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Equal(109_474, work.TotalCharacters);
        Assert.Equal("https://cdn.jiten.moe/aobuta.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);
        Assert.True(work.HasCover);
        Assert.False(work.IsCoverProtected);
        Assert.False(work.IsLinkedToJitenSubdeck);
    }

    [Fact]
    public void LinkToJitenSubdeck_StoresParentAndChildIds_AndSetsSpecifiedCoverSource()
    {
        var work = new MediaWork("Re:Zero volume 1");

        work.LinkToJitenSubdeck(
            54904,
            12345,
            150_000,
            "https://cdn.jiten.moe/rezero-parent.jpg",
            MediaCoverSource.JitenParentFallback);

        Assert.Equal(54904, work.JitenDeckId);
        Assert.Equal(12345, work.JitenSubdeckId);
        Assert.Equal(150_000, work.TotalCharacters);
        Assert.Equal("https://cdn.jiten.moe/rezero-parent.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenParentFallback, work.CoverSource);
        Assert.True(work.HasCover);
        Assert.False(work.IsCoverProtected);
        Assert.True(work.IsLinkedToJitenSubdeck);
    }

    [Fact]
    public void ManualOverride_TakesPriorityOverJitenCount()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(1, 100_000);

        work.ManualCharacterCountOverride = 90_000;

        Assert.Equal(90_000, work.TotalCharacters);
    }

    [Fact]
    public void TtsuCount_TakesPriorityOverJitenAndManualStillWins()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(1, 110_000);

        work.UpdateTtsuCharacterCount(100_000);

        Assert.Equal(100_000, work.TotalCharacters);
        work.ManualCharacterCountOverride = 90_000;
        Assert.Equal(90_000, work.TotalCharacters);
    }

    [Fact]
    public void RemoveJitenLink_ClearsJitenDerivedCover()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenSubdeck(1, 2, 100_000, "https://cdn.jiten.moe/book.jpg", MediaCoverSource.JitenSpecific);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Null(work.JitenCharacterCount);
        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
        Assert.False(work.HasCover);
        Assert.False(work.IsCoverProtected);
    }

    [Fact]
    public void RemoveJitenLink_ClearsJitenParentFallbackCover()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenSubdeck(1, 2, 100_000, "https://cdn.jiten.moe/parent.jpg", MediaCoverSource.JitenParentFallback);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
    }

    [Fact]
    public void RemoveJitenLink_RetainsUserOverrideCover()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(1, 100_000);
        work.UpdateCoverUrl("https://example.com/custom.jpg");
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
        Assert.True(work.IsCoverProtected);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Null(work.JitenCharacterCount);
        Assert.Equal("https://example.com/custom.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
        Assert.True(work.HasCover);
        Assert.True(work.IsCoverProtected);
    }

    [Fact]
    public void RemoveJitenLink_RetainsLegacyUnknownCover()
    {
        var work = new MediaWork("Book");
        TestCoverState.SetLegacyUnknown(work, "https://example.com/legacy.jpg");
        work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/ignore.jpg");

        // The legacy cover was protected during link
        Assert.Equal("https://example.com/legacy.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Equal("https://example.com/legacy.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);
    }

    [Theory]
    [InlineData("http://cdn.jiten.moe/book.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("nocover.jpg")]
    [InlineData("")]
    [InlineData(null)]
    public void LinkToJitenDeck_IgnoresUnsafeOrMissingCoverUrls_LeavingNone(string? coverUrl)
    {
        var work = new MediaWork("Book");

        work.LinkToJitenDeck(1, 100_000, coverUrl);

        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
        Assert.False(work.HasCover);
    }

    [Fact]
    public void Relinking_ReplacesExistingJitenCoverAndProvenance()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/vol1.jpg", MediaCoverSource.JitenSpecific);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);

        work.LinkToJitenDeck(2, 120_000, "https://cdn.jiten.moe/series-fallback.jpg", MediaCoverSource.JitenParentFallback);
        Assert.Equal(2, work.JitenDeckId);
        Assert.Equal(120_000, work.TotalCharacters);
        Assert.Equal("https://cdn.jiten.moe/series-fallback.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenParentFallback, work.CoverSource);
    }

    [Fact]
    public void Relinking_JitenDerivedCoverToNoCover_ClearsStaleJitenCover()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/vol1.jpg", MediaCoverSource.JitenSpecific);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);

        // Relink to deck with no cover / unusable cover
        work.LinkToJitenDeck(2, 110_000, coverUrl: null, coverSource: MediaCoverSource.None);
        Assert.Equal(2, work.JitenDeckId);
        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
    }

    [Fact]
    public void Relinking_PreservesUserOverrideCover_EvenWhenNewJitenHasCoverOrNoCover()
    {
        var work = new MediaWork("Book");
        work.UpdateCoverUrl("https://example.com/user.jpg");
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);

        // Relink with a Jiten cover
        work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/jiten.jpg", MediaCoverSource.JitenSpecific);
        Assert.Equal(1, work.JitenDeckId);
        Assert.Equal(100_000, work.TotalCharacters);
        Assert.Equal("https://example.com/user.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);

        // Relink with no cover
        work.LinkToJitenDeck(2, 120_000, coverUrl: null, coverSource: MediaCoverSource.None);
        Assert.Equal(2, work.JitenDeckId);
        Assert.Equal("https://example.com/user.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
    }

    [Fact]
    public void Relinking_PreservesLegacyUnknownCover_EvenWhenNewJitenHasCoverOrNoCover()
    {
        var work = new MediaWork("Book");
        TestCoverState.SetLegacyUnknown(work, "https://example.com/legacy.jpg");
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);

        // Relink with a Jiten cover
        work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/jiten.jpg", MediaCoverSource.JitenSpecific);
        Assert.Equal(1, work.JitenDeckId);
        Assert.Equal("https://example.com/legacy.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);

        // Relink with no cover
        work.LinkToJitenDeck(2, 120_000, coverUrl: null, coverSource: MediaCoverSource.None);
        Assert.Equal(2, work.JitenDeckId);
        Assert.Equal("https://example.com/legacy.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.LegacyUnknown, work.CoverSource);
    }

    [Fact]
    public void UpdateCoverUrl_SuccessfullyUpdatesValidHttpsUrl_AndSetsUserOverride()
    {
        var work = new MediaWork("Book");

        work.UpdateCoverUrl("https://cdn.jiten.moe/covers/newcover.jpg");

        Assert.Equal("https://cdn.jiten.moe/covers/newcover.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.UserOverride, work.CoverSource);
        Assert.True(work.HasCover);
        Assert.True(work.IsCoverProtected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateCoverUrl_ThrowsOnNullEmptyOrWhitespace(string? invalidUrl)
    {
        var work = new MediaWork("Book");

        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl(invalidUrl!));
    }

    [Theory]
    [InlineData("http://cdn.jiten.moe/insecure.jpg")]
    [InlineData("ftp://cdn.jiten.moe/file.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    [InlineData("nocover.jpg")]
    public void UpdateCoverUrl_ThrowsOnNonHttpsOrInvalidUrl(string invalidUrl)
    {
        var work = new MediaWork("Book");

        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl(invalidUrl));
    }

    [Fact]
    public void UpdateCoverUrl_ThrowsWhenExceedingMaxLength()
    {
        var work = new MediaWork("Book");
        var overlong = "https://example.com/" + new string('a', 2050) + ".jpg";

        Assert.Throws<ArgumentException>(() => work.UpdateCoverUrl(overlong));
    }

    [Fact]
    public void LinkToJitenDeck_ThrowsOnInvalidCoverSource()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenDeck(
            7,
            70_000,
            "https://cdn.jiten.moe/original.jpg",
            MediaCoverSource.JitenSpecific);

        Assert.Throws<ArgumentException>(() =>
            work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/cover.jpg", (MediaCoverSource)99));

        Assert.Throws<ArgumentException>(() =>
            work.LinkToJitenDeck(1, 100_000, "https://cdn.jiten.moe/cover.jpg", MediaCoverSource.UserOverride));

        Assert.Equal(7, work.JitenDeckId);
        Assert.Equal(70_000, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/original.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);
    }
}
