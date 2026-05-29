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

    public static BrainConfig LoadFrom(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BrainConfig>(json)
               ?? throw new InvalidOperationException("Failed to deserialise AriBrain.json");
    }
}
