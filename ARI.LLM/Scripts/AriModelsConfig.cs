using System.Text.Json;

namespace ARI.LLM;

internal class AriModelsConfig
{
    public List<ModelConfig> Models { get; init; }

    /// <summary>
    /// How often (in minutes) to sweep threads for new Engram activity.
    /// 0 = disabled. Any other value = sweep every N minutes, but only if the thread has new messages.
    /// </summary>
    public int EngramSweepIntervalMinutes { get; init; } = 0;

    /// <summary>
    /// How many rounds of recursive note fetching Engram can do before extraction.
    /// Each round lets Engram follow references found in previously fetched notes.
    /// Default 7. Set to 1 to disable recursive fetching.
    /// </summary>
    public int EngramFetchDepth { get; init; } = 7;

    internal static AriModelsConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"AriModels.json not found at {path}");

        string json = File.ReadAllText(path);
        AriModelsConfig result = JsonSerializer.Deserialize<AriModelsConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (result == null)
            throw new InvalidOperationException("Failed to deserialise AriModels.json.");

        return result;
    }
}

internal class ModelConfig
{
    public string Name { get; init; }
    public string Endpoint { get; init; }
    public string Model { get; init; }
    public string SystemPrompt { get; init; }
    public int ShortTermMemoryLimit { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Maximum tokens to generate per response. -1 = unlimited.</summary>
    public int MaxTokens { get; init; } = -1;

    /// <summary>Optional prompt sent as the final extraction step in Engram. Loaded from config so it can be tuned without recompiling.</summary>
    public string? ExtractionPrompt { get; init; }
}
