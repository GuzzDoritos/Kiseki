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
            0);

        selection.ApplyTo(work, JitenTitleChoice.English);

        Assert.Equal("Volume 1", work.Title);
        Assert.Equal(10, work.JitenDeckId);
        Assert.Equal(11, work.JitenSubdeckId);
        Assert.Equal(123_456, work.JitenCharacterCount);
        Assert.Equal("https://cdn.jiten.moe/volume-1.jpg", work.JitenCoverUrl);
    }
}
