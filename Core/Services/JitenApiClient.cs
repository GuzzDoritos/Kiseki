namespace Core.Services;

using Core.DTOs;
using System.Net.Http.Json;
using System.Text.Json;

public class JitenApiClient
{
    private static readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("https://api.jiten.moe/")
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<JitenDeckDTO?> GetMediaFromQueryAsync(string query)
    {
        try
        {
            string encodedQuery = Uri.EscapeDataString(query);
            string endpoint = $"api/media-deck/get-media-decks?offset=0&mediaType=4&wordId=0&readingIndex=0&titleFilter={encodedQuery}&sortOrder=0";

            var response = await _httpClient.GetFromJsonAsync<JitenResponseContainerDTO>(endpoint, _jsonOptions);

            return response?.Data.FirstOrDefault();
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"[JitenApiClient] HTTP Request failed: {ex.Message}");
            return null;
        }
    }
}