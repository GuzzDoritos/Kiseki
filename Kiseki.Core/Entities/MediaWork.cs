namespace Kiseki.Core.Entities;

public class MediaWork
{
    // Parameterless constructor for EF Core / Serialization
    protected MediaWork() { }

    // Primary domain constructor
    public MediaWork(
        string title,
        int? jitenDeckId = null,
        int? jitenCharCount = null,
        MediaType mediaType = MediaType.Book)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title;
        MediaType = mediaType;
        JitenDeckId = jitenDeckId;
        JitenCharacterCount = jitenCharCount;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public MediaType MediaType { get; set; } = MediaType.Book;

    public Guid? MediaSeriesId { get; set; }
    public MediaSeries? MediaSeries { get; set; }

    public int? JitenDeckId { get; set; }
    public int? JitenSubdeckId { get; private set; }

    // Character Counts
    public int? JitenCharacterCount { get; set; }
    public int? ManualCharacterCountOverride { get; set; }

    public bool HasJitenLink => JitenDeckId.HasValue;
    public bool IsLinkedToJitenSubdeck => JitenSubdeckId.HasValue;

    // The effective count uses the manual override if provided, otherwise Jiten's count
    public int TotalCharacters => ManualCharacterCountOverride ?? JitenCharacterCount ?? 0;

    // Status Override
    public bool IsCompleted { get; set; }

    public List<ImmersionLog> Logs { get; set; } = new();

    public void LinkToJitenDeck(int deckId, int characterCount)
    {
        ValidateJitenLinkValues(deckId, characterCount);

        JitenDeckId = deckId;
        JitenSubdeckId = null;
        JitenCharacterCount = characterCount;
    }

    public void LinkToJitenSubdeck(
        int parentDeckId,
        int subdeckId,
        int characterCount)
    {
        ValidateJitenLinkValues(parentDeckId, characterCount);

        if (subdeckId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subdeckId), "Subdeck ID must be positive.");
        }

        JitenDeckId = parentDeckId;
        JitenSubdeckId = subdeckId;
        JitenCharacterCount = characterCount;
    }

    public void RemoveJitenLink()
    {
        JitenDeckId = null;
        JitenSubdeckId = null;
        JitenCharacterCount = null;
    }

    private static void ValidateJitenLinkValues(int deckId, int characterCount)
    {
        if (deckId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deckId), "Deck ID must be positive.");
        }

        if (characterCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterCount), "Character count cannot be negative.");
        }
    }

    public int CurrentCharactersRead => Logs.Sum(l => l.CharactersRead);

    // Dynamic Progress Calculation
    public double ProgressPercentage
    {
        get
        {
            // 1. If explicitly marked completed, force 100%
            if (IsCompleted) return 100.0;

            // 2. If no total characters are set, we cannot calculate %
            if (TotalCharacters == 0) return 0.0;

            // 3. Otherwise calculate percentage capped at 100%
            return Math.Min(100.0, ((double)CurrentCharactersRead / TotalCharacters) * 100.0);
        }
    }
}
