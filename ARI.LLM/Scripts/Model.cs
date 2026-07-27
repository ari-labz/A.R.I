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

    // Sampler settings (temp/topP/topK/minP/repeatPenalty/...) live on Server, not here — a server
    // only ever runs one model at a time, and sampling is an inference-serving concern, not a model
    // metadata concern. See Server.cs.

    [JsonPropertyName("jinja")]
    public bool Jinja { get; set; } = true;

    /// <summary>Path to a custom jinja template file, relative to the ARI Server directory.
    /// When set, overrides the model's built-in metadata template and --jinja is still passed
    /// so the engine accepts an arbitrary template rather than only the built-in named set.</summary>
    [JsonPropertyName("chatTemplatePath")]
    public string? ChatTemplatePath { get; set; }

    // Model metadata
    [JsonPropertyName("modelSize")]
    public string ModelSize { get; set; } = "";

    [JsonPropertyName("moe")]
    public bool MoE { get; set; }

    [JsonPropertyName("mtp")]
    public bool MTP { get; set; }

    [JsonIgnore]
    public bool Downloaded { get; set; }

    [JsonIgnore]
    public long FileSizeBytes { get; set; }

    [JsonIgnore]
    public GgufReader.KvArchParams? KvArch { get; set; }

    public void RefreshDownloadedState(string modelsPath)
    {
        var fullPath = System.IO.Path.Combine(modelsPath, Path);
        Downloaded = File.Exists(fullPath);
        if (Downloaded)
        {
            FileSizeBytes = new FileInfo(fullPath).Length;
            KvArch = GgufReader.TryRead(fullPath);
        }
        else
        {
            FileSizeBytes = 0;
            KvArch = null;
        }
    }
}
