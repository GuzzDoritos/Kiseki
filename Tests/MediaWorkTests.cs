using Core.Entities;

namespace Tests;

public class MediaWorkTests
{
    [Fact]
    public void LinkToJitenDeck_UsesTheWholeDeckCount()
    {
        var work = new MediaWork("Aobuta volume 1");

        work.LinkToJitenDeck(95367, 109_474);

        Assert.Equal(95367, work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Equal(109_474, work.TotalCharacters);
        Assert.False(work.IsLinkedToJitenSubdeck);
    }

    [Fact]
    public void LinkToJitenSubdeck_StoresParentAndChildIds()
    {
        var work = new MediaWork("Re:Zero volume 1");

        work.LinkToJitenSubdeck(54904, 12345, 150_000);

        Assert.Equal(54904, work.JitenDeckId);
        Assert.Equal(12345, work.JitenSubdeckId);
        Assert.Equal(150_000, work.TotalCharacters);
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
    public void RemoveJitenLink_ClearsAllExternalValues()
    {
        var work = new MediaWork("Book");
        work.LinkToJitenSubdeck(1, 2, 100_000);

        work.RemoveJitenLink();

        Assert.Null(work.JitenDeckId);
        Assert.Null(work.JitenSubdeckId);
        Assert.Null(work.JitenCharacterCount);
    }
}
