using System.Text.Json;

namespace ARI.LLM;

internal class AriModelsConfig
{
    public List<ModelConfig> Models { get; init; }

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
    public bool Enabled { get; init; }

    /// <summary>Only applies to Dialogue. Number of past messages to include in each prompt.</summary>
    public int ShortTermMemoryLimit { get; init; }

    /// <summary>Maximum tokens to generate per response. -1 = unlimited.</summary>
    public int MaxTokens { get; init; } = -1;

    /// <summary>Optional prompt sent as the final extraction step in Engram.</summary>
    public string? ExtractionPrompt { get; init; }

    /// <summary>
    /// Engram only. How often (in minutes) to sweep threads for new activity.
    /// 0 = disabled.
    /// </summary>
    public int SweepIntervalMinutes { get; init; } = 0;

    /// <summary>
    /// Engram and Recall. How many rounds of recursive note fetching to perform.
    /// Each round lets the model follow references found in previously fetched notes.
    /// Set to 0 to disable recursive fetching (Recall: disables Recall entirely).
    /// </summary>
    public int RecursiveBrainSearchDepth { get; init; } = 0;

    /// <summary>
    /// Recall only. Maximum number of notes to keep in the in-memory MRU cache.
    /// Set to 0 to disable caching.
    /// </summary>
    public int CacheSize { get; init; } = 0;
}
