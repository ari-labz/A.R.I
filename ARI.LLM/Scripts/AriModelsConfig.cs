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
}
