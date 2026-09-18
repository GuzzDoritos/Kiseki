using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;

namespace Kiseki.Tests;

public sealed class JitenMediaSelectionTests
{
    [Fact]
    public void FromSubdeck_UsesParentCoverWhenTheChildHasNone()
    {
        var parent = new JitenDeckDTO
        {
            DeckId = 10,
            OriginalTitle = "Series",
            CoverName = "https://cdn.jiten.moe/series.jpg"
        };
        var subdeck = new JitenDeckDTO
        {
            DeckId = 11,
            OriginalTitle = "第一巻",
            EnglishTitle = "Volume 1",
            CharacterCount = 123_456
        };

        var selection = JitenMediaSelection.FromSubdeck(parent, subdeck);

        Assert.Equal(10, selection.DeckId);
        Assert.Equal(11, selection.SubdeckId);
        Assert.Equal(123_456, selection.CharacterCount);
        Assert.Equal("https://cdn.jiten.moe/series.jpg", selection.CoverUrl);
        Assert.Equal(JitenCoverEvidence.ParentFallback, selection.CoverEvidence);
    }

    [Fact]
    public void FromSubdeck_UsesChildCoverWhenAvailable()
    {
        var parent = new JitenDeckDTO
        {
            DeckId = 10,
            OriginalTitle = "Series",
            CoverName = "https://cdn.jiten.moe/series.jpg"
        };
        var subdeck = new JitenDeckDTO
        {
            DeckId = 11,
            OriginalTitle = "第一巻",
            EnglishTitle = "Volume 1",
            CharacterCount = 123_456,
            CoverName = "https://cdn.jiten.moe/volume-1.jpg"
        };

        var selection = JitenMediaSelection.FromSubdeck(parent, subdeck);

        Assert.Equal("https://cdn.jiten.moe/volume-1.jpg", selection.CoverUrl);
        Assert.Equal(JitenCoverEvidence.Specific, selection.CoverEvidence);
    }

    [Fact]
    public void FromDeck_SetsNoneWhenNoCoverExists()
    {
        var deck = new JitenDeckDTO
        {
            DeckId = 10,
            OriginalTitle = "Standalone Book",
            CoverName = "nocover.jpg"
        };

        var selection = JitenMediaSelection.FromDeck(deck);

        Assert.Null(selection.CoverUrl);
        Assert.Equal(JitenCoverEvidence.None, selection.CoverEvidence);
    }

    [Fact]
    public void ApplyTo_LinksTheSelectionAndUsesTheRequestedTitle()
    {
        var work = new MediaWork("Local title");
        var selection = new JitenMediaSelection(
            10,
            11,
            "第一巻",
            "Dai Ikkan",
            "Volume 1",
            123_456,
            "https://cdn.jiten.moe/volume-1.jpg",
            0,
            JitenCoverEvidence.Specific);

        selection.ApplyTo(work, JitenTitleChoice.English);

        Assert.Equal("Volume 1", work.Title);
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(11, work.JitenSubdeckId);
        Assert.Equal(123_456, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/volume-1.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenSpecific, work.CoverSource);
    }

    [Fact]
    public void ApplyTo_WithParentFallback_PersistsJitenParentFallbackProvenance()
    {
        var work = new MediaWork("Local title");
        var selection = new JitenMediaSelection(
            10,
            11,
            "第一巻",
            "Dai Ikkan",
            "Volume 1",
            123_456,
            "https://cdn.jiten.moe/series-fallback.jpg",
            0,
            JitenCoverEvidence.ParentFallback);

        selection.ApplyTo(work, JitenTitleChoice.KeepCurrent);

        Assert.Equal("https://cdn.jiten.moe/series-fallback.jpg", work.CoverUrl);
        Assert.Equal(MediaCoverSource.JitenParentFallback, work.CoverSource);
    }

    [Fact]
    public void ApplyTo_WithNoneEvidence_LeavesCoverNullAndProvenanceNone()
    {
        var work = new MediaWork("Local title");
        var selection = new JitenMediaSelection(
            10,
            null,
            "Standalone Book",
            "Standalone Book",
            "Standalone Book",
            80_000,
            null,
            0,
            JitenCoverEvidence.None);

        selection.ApplyTo(work, JitenTitleChoice.KeepCurrent);

        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
    }

    [Fact]
    public void ApplyTo_WithNoneEvidence_ClearsAStaleJitenDerivedCover()
    {
        var work = new MediaWork("Local title");
        work.LinkToJitenDeck(
            5,
            50_000,
            "https://cdn.jiten.moe/old-cover.jpg",
            MediaCoverSource.JitenSpecific);
        var selection = new JitenMediaSelection(
            10,
            null,
            "Replacement",
            "Replacement",
            "Replacement",
            80_000,
            null,
            0,
            JitenCoverEvidence.None);

        selection.ApplyTo(work, JitenTitleChoice.KeepCurrent);

        Assert.Equal(10, work.JitenDeckId);
        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
    }

    [Fact]
    public void ApplyTo_WithUnsupportedCoverEvidence_DoesNotMutateTheWork()
    {
        var work = new MediaWork("Local title");
        var selection = new JitenMediaSelection(
            10,
            null,
            "Replacement",
            "Replacement",
            "Replacement",
            80_000,
            null,
            0,
            (JitenCoverEvidence)99);

        Assert.Throws<InvalidOperationException>(() =>
            selection.ApplyTo(work, JitenTitleChoice.KeepCurrent));
        Assert.False(work.HasJitenLink);
        Assert.Null(work.CoverUrl);
        Assert.Equal(MediaCoverSource.None, work.CoverSource);
    }
}
