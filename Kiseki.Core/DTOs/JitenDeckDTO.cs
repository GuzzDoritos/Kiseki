using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Kiseki.Core.DTOs
{
    public class JitenResponseContainerDTO
    {
        [JsonPropertyName("data")]
        public List<JitenDeckDTO> Data { get; set; } = new();

        [JsonPropertyName("totalItems")]
        public int TotalItems { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("currentOffset")]
        public int CurrentOffset { get; set; }
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

        [JsonPropertyName("parentDeckId")]
        public int? ParentDeckId { get; set; }

        [JsonPropertyName("childrenDeckCount")]
        public int ChildrenDeckCount { get; set; }

    }

    public class JitenDeckDetailResponseDTO
    {
        [JsonPropertyName("data")]
        public JitenDeckDetailDTO? Data { get; set; }

        [JsonPropertyName("totalItems")]
        public int TotalItems { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("currentOffset")]
        public int CurrentOffset { get; set; }
    }

    public class JitenDeckDetailDTO
    {
        [JsonPropertyName("parentDeck")]
        public JitenDeckDTO? ParentDeck { get; set; }

        [JsonPropertyName("mainDeck")]
        public JitenDeckDTO? MainDeck { get; set; }

        [JsonPropertyName("subDecks")]
        public List<JitenDeckDTO> SubDecks { get; set; } = [];
    }

    public class JitenFranchiseDTO
    {
        [JsonPropertyName("nodes")]
        public List<JitenFranchiseNodeDTO> Nodes { get; set; } = [];

        [JsonPropertyName("edges")]
        public List<JitenFranchiseEdgeDTO> Edges { get; set; } = [];

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }
    }

    public class JitenFranchiseNodeDTO
    {
        [JsonPropertyName("deckId")]
        public int DeckId { get; set; }

        [JsonPropertyName("originalTitle")]
        public string OriginalTitle { get; set; } = string.Empty;

        [JsonPropertyName("englishTitle")]
        public string EnglishTitle { get; set; } = string.Empty;

        [JsonPropertyName("mediaType")]
        public int MediaType { get; set; }

        [JsonPropertyName("characterCount")]
        public int CharacterCount { get; set; }

        [JsonPropertyName("childrenDeckCount")]
        public int ChildrenDeckCount { get; set; }
    }

    public class JitenFranchiseEdgeDTO
    {
        [JsonPropertyName("sourceDeckId")]
        public int SourceDeckId { get; set; }

        [JsonPropertyName("targetDeckId")]
        public int TargetDeckId { get; set; }

        [JsonPropertyName("relationshipType")]
        public int RelationshipType { get; set; }
    }
}
