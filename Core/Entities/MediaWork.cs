namespace Core.Entities;

public class MediaWork
{
    // Parameterless constructor for EF Core / Serialization
    protected MediaWork() { }

    // Primary domain constructor
    public MediaWork(string title, int? jitenDeckId = null, int? jitenCharCount = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        Title = title;
        JitenDeckId = jitenDeckId;
        JitenCharacterCount = jitenCharCount;
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;

    public int? JitenDeckId { get; set; }

    // Character Counts
    public int? JitenCharacterCount { get; set; }
    public int? ManualCharacterCountOverride { get; set; }

    // The effective count uses the manual override if provided, otherwise Jiten's count
    public int TotalCharacters => ManualCharacterCountOverride ?? JitenCharacterCount ?? 0;

    // Status Override
    public bool IsCompleted { get; set; }

    public List<ImmersionLog> Logs { get; set; } = new();

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