using System.Text.Json.Serialization;

namespace Kiseki.Core.DTOs;

public sealed class TtsuReaderDTO
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("dateKey")]
    public string DateKey { get; set; } = string.Empty;

    [JsonPropertyName("charactersRead")]
    public int CharactersRead { get; set; }

    [JsonPropertyName("readingTime")]
    public double ReadingTime { get; set; }

    [JsonPropertyName("lastStatisticModified")]
    public long LastStatisticModified { get; set; }
}

public sealed class TtsuBookContainer
{
    public string Title { get; set; } = string.Empty;
    public List<TtsuReaderDTO> Entries { get; set; } = [];
}
