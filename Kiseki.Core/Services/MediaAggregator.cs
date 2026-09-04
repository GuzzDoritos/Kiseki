namespace Kiseki.Core.Services;

using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;

public class MediaAggregator
{
    private readonly List<MediaWork> _works = new();
    private readonly IJitenApiClient _jitenApiClient;

    public MediaAggregator(IJitenApiClient jitenApiClient)
    {
        _jitenApiClient = jitenApiClient;
    }

    public async Task<MediaWork> ImportTtsuSessionsAsync(List<TtsuReaderDTO> ttsuEntries)
    {
        if (!ttsuEntries.Any())
            throw new ArgumentException("No ttsu entries provided.");

        string bookTitle = ttsuEntries.First().Title;

        // Attempt Jiten lookup (returns null if not found)
        JitenDeckDTO? jitenDeck = (await _jitenApiClient.SearchBooksAsync(bookTitle))
            .FirstOrDefault();

        var work = new MediaWork(bookTitle)
        {
            Title = bookTitle,
            JitenDeckId = jitenDeck?.DeckId,
            JitenCharacterCount = jitenDeck?.CharacterCount, // Null if book isn't on Jiten
            Logs = ttsuEntries
                .Select(entry => entry.ToImmersionLog())
                .ToList()
        };

        _works.Add(work);
        return work;
    }
}
