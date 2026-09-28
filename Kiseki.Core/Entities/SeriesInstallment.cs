namespace Kiseki.Core.Entities;

public class SeriesInstallment
{
    // Parameterless constructor for EF Core / Serialization
    protected SeriesInstallment() { }

    public SeriesInstallment(
        Guid mediaSeriesId,
        int sequenceNumber,
        string title,
        int? jitenSubdeckId = null,
        int jitenCharacterCount = 0,
        string? coverUrl = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        MediaSeriesId = mediaSeriesId;
        SequenceNumber = sequenceNumber;
        Title = title.Trim();
        JitenSubdeckId = jitenSubdeckId;
        JitenCharacterCount = Math.Max(0, jitenCharacterCount);
        CoverUrl = coverUrl;
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MediaSeriesId { get; set; }
    public MediaSeries? MediaSeries { get; set; }

    public int SequenceNumber { get; set; }
    public string Title { get; set; } = string.Empty;

    public int? JitenSubdeckId { get; set; }
    public int JitenCharacterCount { get; set; }

    public string? CoverUrl { get; set; }

    public Guid? MediaWorkId { get; set; }
    public MediaWork? MediaWork { get; set; }

    // Total Characters: MediaWork TotalCharacters (if present and > 0) overrides Jiten estimate.
    public int EffectiveTotalCharacters =>
        (MediaWork != null && MediaWork.TotalCharacters > 0)
            ? MediaWork.TotalCharacters
            : JitenCharacterCount;

    // Characters Read:
    // If MediaWork is completed, counts 100% of EffectiveTotalCharacters.
    // Otherwise, counts CurrentCharactersRead from logs.
    public int EffectiveCharactersRead
    {
        get
        {
            if (MediaWork == null) return 0;
            if (MediaWork.IsCompleted) return EffectiveTotalCharacters;
            return MediaWork.CurrentCharactersRead;
        }
    }

    public bool IsCompleted =>
        MediaWork != null && (MediaWork.IsCompleted || (EffectiveTotalCharacters > 0 && EffectiveCharactersRead >= EffectiveTotalCharacters));

    public double ProgressPercentage
    {
        get
        {
            if (IsCompleted) return 100.0;
            if (EffectiveTotalCharacters == 0) return 0.0;
            return Math.Min(100.0, ((double)EffectiveCharactersRead / EffectiveTotalCharacters) * 100.0);
        }
    }

    // Cover precedence: prefer MediaWork cover if it has one, otherwise fallback to installment's own Jiten cover.
    public string? EffectiveCoverUrl =>
        MediaWork?.HasCover == true ? MediaWork.CoverUrl : CoverUrl;
}

