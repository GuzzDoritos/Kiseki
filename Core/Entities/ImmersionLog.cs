namespace Core.Entities;

public class ImmersionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public int CharactersRead { get; set; }
    public double TimeSpentMinutes { get; set; }
    public string Source { get; set; } = "ttsu";
}