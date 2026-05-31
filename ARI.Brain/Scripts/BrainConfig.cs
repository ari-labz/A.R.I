using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.Brain;

public class BrainConfig
{
    [JsonPropertyName("TriliumUrl")]
    public string TriliumUrl { get; set; } = "http://localhost:8080";

    [JsonPropertyName("EtapiToken")]
    public string EtapiToken { get; set; } = string.Empty;

    [JsonPropertyName("RootNoteId")]
    public string RootNoteId { get; set; } = "root";

    /// <summary>
    /// Maximum number of note contents held in BrainService's in-memory cache.
    /// Most-recently used notes are at the front; when full the oldest is evicted.
    /// Set to 0 to disable caching. Default 500.
    /// </summary>
    [JsonPropertyName("ContentCacheSize")]
    public int ContentCacheSize { get; set; } = 500;

    public static BrainConfig LoadFrom(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BrainConfig>(json)
               ?? throw new InvalidOperationException("Failed to deserialise AriBrain.json");
    }
}
