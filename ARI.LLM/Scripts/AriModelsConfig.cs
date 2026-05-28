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
}
