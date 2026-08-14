namespace Core.Services;

using Core.DTOs;
using Core.Entities;

public class MediaAggregator
{
    private readonly List<MediaWork> _works = new();

    public async Task<MediaWork> ImportTtsuSessionsAsync(List<TtsuReaderDTO> ttsuEntries)
    {
        if (!ttsuEntries.Any())
            throw new ArgumentException("No ttsu entries provided.");

        string bookTitle = ttsuEntries.First().Title;

        // Attempt Jiten lookup (returns null if not found)
        JitenDeckDTO? jitenDeck = await JitenApiClient.GetMediaFromQueryAsync(bookTitle);

        var work = new MediaWork(bookTitle)
        {
            Title = bookTitle,
            JitenDeckId = jitenDeck?.DeckId,
            JitenCharacterCount = jitenDeck?.CharacterCount, // Null if book isn't on Jiten
            Logs = ttsuEntries.Select(e => new ImmersionLog
            {
                Date = DateOnly.Parse(e.DateKey),
                CharactersRead = e.CharactersRead,
                TimeSpentMinutes = e.ReadingTime,
                Source = "ttsu"
            }).ToList()
        };

        _works.Add(work);
        return work;
    }
}