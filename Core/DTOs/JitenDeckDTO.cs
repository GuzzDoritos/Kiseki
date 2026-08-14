using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Core.DTOs
{
    public class JitenResponseContainerDTO
    {
        [JsonPropertyName("data")]
        public List<JitenDeckDTO> Data { get; set; } = new();

        [JsonPropertyName("totalItems")]
        public int TotalItems { get; set; }
    }

    public class JitenDeckDTO
    {
        [JsonPropertyName("deckId")]
        public int DeckId { get; set; }

        [JsonPropertyName("releaseDate")]
        public DateTime? ReleaseDate { get; set; }

        [JsonPropertyName("coverName")]
        public string CoverName { get; set; } = string.Empty;

        [JsonPropertyName("mediaType")]
        public int MediaType { get; set; }

        [JsonPropertyName("originalTitle")]
        public string OriginalTitle { get; set; } = string.Empty;

        [JsonPropertyName("romajiTitle")]
        public string RomajiTitle { get; set; } = string.Empty;

        [JsonPropertyName("englishTitle")]
        public string EnglishTitle { get; set; } = string.Empty;

        [JsonPropertyName("characterCount")]
        public int CharacterCount { get; set; }

    }
}
