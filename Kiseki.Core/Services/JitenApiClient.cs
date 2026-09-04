using Kiseki.Core.DTOs;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kiseki.Core.Services;

public sealed class JitenApiClient : IJitenApiClient
{
    private static readonly Uri BaseAddress = new("https://api.jiten.moe/");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public JitenApiClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= BaseAddress;
    }

    public async Task<IReadOnlyList<JitenDeckDTO>> SearchBooksAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var encodedQuery = Uri.EscapeDataString(query.Trim());
        var firstPage = await GetSearchPageAsync(encodedQuery, 0);

        if (firstPage is null)
        {
            return [];
        }

        var results = firstPage.Data;
        var offset = firstPage.PageSize > 0
            ? firstPage.CurrentOffset + firstPage.PageSize
            : results.Count;

        while (results.Count < firstPage.TotalItems && offset > 0)
        {
            var nextPage = await GetSearchPageAsync(encodedQuery, offset);

            if (nextPage is null || nextPage.Data.Count == 0)
            {
                break;
            }

            results.AddRange(nextPage.Data);
            offset += nextPage.PageSize > 0
                ? nextPage.PageSize
                : nextPage.Data.Count;
        }

        return results;
    }

    public async Task<JitenDeckDetailDTO?> GetDeckDetailAsync(int deckId)
    {
        if (deckId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deckId));
        }

        var firstPage = await GetDeckDetailPageAsync(deckId, 0);

        if (firstPage?.Data is null)
        {
            return null;
        }

        var detail = firstPage.Data;
        var offset = firstPage.PageSize > 0
            ? firstPage.CurrentOffset + firstPage.PageSize
            : detail.SubDecks.Count;

        while (detail.SubDecks.Count < firstPage.TotalItems && offset > 0)
        {
            var nextPage = await GetDeckDetailPageAsync(deckId, offset);

            if (nextPage?.Data is null || nextPage.Data.SubDecks.Count == 0)
            {
                break;
            }

            detail.SubDecks.AddRange(nextPage.Data.SubDecks);
            offset += nextPage.PageSize > 0
                ? nextPage.PageSize
                : nextPage.Data.SubDecks.Count;
        }

        return detail;
    }

    public async Task<JitenFranchiseDTO?> GetFranchiseAsync(int deckId)
    {
        if (deckId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deckId));
        }

        using var response = await _httpClient.GetAsync(
            $"api/media-deck/{deckId}/franchise");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JitenFranchiseDTO>(JsonOptions);
    }

    private async Task<JitenDeckDetailResponseDTO?> GetDeckDetailPageAsync(
        int deckId,
        int offset)
    {
        using var response = await _httpClient.GetAsync(
            $"api/media-deck/{deckId}/detail?offset={offset}");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JitenDeckDetailResponseDTO>(JsonOptions);
    }

    private Task<JitenResponseContainerDTO?> GetSearchPageAsync(
        string encodedQuery,
        int offset)
    {
        var endpoint =
            "api/media-deck/get-media-decks" +
            $"?offset={offset}&mediaType=4&wordId=0&readingIndex=0" +
            $"&titleFilter={encodedQuery}&sortOrder=0";

        return _httpClient.GetFromJsonAsync<JitenResponseContainerDTO>(
            endpoint, JsonOptions);
    }
}
