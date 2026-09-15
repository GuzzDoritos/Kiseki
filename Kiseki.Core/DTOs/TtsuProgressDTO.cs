using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiseki.Core.DTOs;

public sealed class TtsuProgressDTO
{
    [JsonPropertyName("dataId")]
    public long? DataId { get; set; }

    [JsonPropertyName("exploredCharCount")]
    public int? ExploredCharacterCount { get; set; }

    [JsonPropertyName("progress")]
    public JsonElement Progress { get; set; }

    [JsonPropertyName("lastBookmarkModified")]
    public long? LastBookmarkModified { get; set; }

    [JsonIgnore]
    public int? ExporterVersion { get; set; }

    [JsonIgnore]
    public int? DatabaseVersion { get; set; }
}
