namespace Core.Entities;

public class MediaSeries
{
    protected MediaSeries() { }

    public MediaSeries(string title, MediaType mediaType)
    {
        SetTitle(title);
        MediaType = mediaType;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; private set; } = string.Empty;
    public MediaType MediaType { get; set; }

    public Guid? FranchiseId { get; set; }
    public Franchise? Franchise { get; set; }

    // Set when one Jiten parent deck represents this entire medium-specific series.
    public int? JitenDeckId { get; set; }

    public List<MediaWork> Works { get; set; } = [];

    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title.Trim();
    }
}
