using System.Text.Json.Serialization;

namespace ARI.LLM;

public class Model
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Path to the .gguf file, relative to modelsPath. Includes subdirectory and extension.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Path to the mmproj .gguf file, relative to modelsPath. Only set for multimodal models.</summary>
    [JsonPropertyName("mmprojPath")]
    public string MmprojPath { get; set; } = "";

    // Download sources (HuggingFace or direct URL)
    [JsonPropertyName("downloadLink")]
    public string DownloadLink { get; set; } = "";

    [JsonPropertyName("mmprojDownloadLink")]
    public string MmprojDownloadLink { get; set; } = "";

    // Inference settings — stored on the model, applied at server startup
    [JsonPropertyName("temp")]
    public float Temp { get; set; } = 0.6f;

    [JsonPropertyName("topP")]
    public float TopP { get; set; } = 0.95f;

    [JsonPropertyName("topK")]
    public int TopK { get; set; } = 40;

    [JsonPropertyName("minP")]
    public float MinP { get; set; } = 0.0f;

    [JsonPropertyName("repeatPenalty")]
    public float RepeatPenalty { get; set; } = 1.0f;

    [JsonPropertyName("jinja")]
    public bool Jinja { get; set; } = true;

    // Model metadata
    [JsonPropertyName("modelSize")]
    public string ModelSize { get; set; } = "";

    [JsonPropertyName("moe")]
    public bool MoE { get; set; }

    [JsonPropertyName("mtp")]
    public bool MTP { get; set; }

    [JsonIgnore]
    public bool Downloaded { get; set; }

    public void RefreshDownloadedState(string modelsPath)
    {
        Downloaded = File.Exists(System.IO.Path.Combine(modelsPath, Path));
    }
}
