namespace Core.Entities;

public class Franchise
{
    protected Franchise() { }

    public Franchise(string title, int? jitenAnchorDeckId = null)
    {
        SetTitle(title);
        JitenAnchorDeckId = jitenAnchorDeckId;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; private set; } = string.Empty;

    // Jiten franchises are connected deck graphs and do not have their own ID.
    // Any deck in the graph can act as an anchor for loading it again.
    public int? JitenAnchorDeckId { get; set; }

    public List<MediaSeries> Series { get; set; } = [];

    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title.Trim();
    }
}
