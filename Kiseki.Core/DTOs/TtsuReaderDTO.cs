using System.Text.Json.Serialization;

namespace Kiseki.Core.DTOs
{
    public class TtsuReaderDTO
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("dateKey")]
        public string DateKey { get; set; } = string.Empty; // e.g. "2026-08-05"

        [JsonPropertyName("charactersRead")]
        public int CharactersRead { get; set; }

        [JsonPropertyName("readingTime")]
        public double ReadingTime { get; set; } // in seconds

        [JsonPropertyName("lastStatisticModified")]
        public long LastStatisticModified { get; set; }
    }

    public class TtsuBookContainer
    {
        public string Title { get; set; }
        public List<TtsuReaderDTO> Entries {  get; set; }
    }
}
