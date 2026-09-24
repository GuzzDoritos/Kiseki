namespace Kiseki.Core.Services;

using Kiseki.Core.DTOs;
using Kiseki.Core.Entities;
using Kiseki.Core.Models;

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

        var work = MediaCatalogService.CreateDetachedTrackedCopy(bookTitle, MediaType.Book);
        work.Logs = ttsuEntries
            .Select(entry => entry.ToImmersionLog())
            .ToList();

        if (jitenDeck is not null)
        {
            MediaCatalogService.LinkDetachedCopyToJiten(work, JitenMediaSelection.FromDeck(jitenDeck));
        }

        _works.Add(work);
        return work;
    }
}
