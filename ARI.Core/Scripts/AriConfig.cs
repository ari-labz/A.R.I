using System.Text.Json;

namespace ARI.Core.Scripts;

public class AriConfig
{
    public LlmConfig LLM { get; init; }
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }

    public static AriConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
            throw new Exception($"AriConfig.json not found at {path}");

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AriConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new Exception("Failed to deserialise AriConfig.json.");
    }
}

public class LlmConfig
{
    public string Model { get; init; }
    public string Endpoint { get; init; }
}

public class TriliumConfig
{
    public string Endpoint { get; init; }
}

public class DockerConfig
{
    public string ComposePath { get; init; }
}