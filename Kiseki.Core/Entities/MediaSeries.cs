namespace Kiseki.Core.Entities;

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
    public string? CoverUrl { get; set; }

    public List<MediaWork> Works { get; set; } = [];
    public List<SeriesInstallment> Installments { get; set; } = [];

    public int TotalCharacters => Installments.Sum(i => i.EffectiveTotalCharacters);
    public int CurrentCharactersRead => Installments.Sum(i => i.EffectiveCharactersRead);
    public int CompletedInstallmentsCount => Installments.Count(i => i.IsCompleted);

    public double ProgressPercentage => TotalCharacters == 0
        ? 0.0
        : Math.Min(100.0, ((double)CurrentCharactersRead / TotalCharacters) * 100.0);

    public string? EffectiveCoverUrl =>
        !string.IsNullOrWhiteSpace(CoverUrl)
            ? CoverUrl
            : Installments.OrderBy(i => i.SequenceNumber).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.EffectiveCoverUrl))?.EffectiveCoverUrl;

    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title.Trim();
    }
}
