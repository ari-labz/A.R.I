using System.Text.Json;

namespace ARI.Core.Scripts;

public class AriConfig
{
    public LlmConfig LLM { get; init; }
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }
    public ModulesConfig Modules { get; init; }

    public static AriConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            Common.Logger.LogCritical($"AriConfig.json not found at {path}");
            throw new Exception($"AriConfig.json not found at {path}");
        }

        string json = File.ReadAllText(path);
        AriConfig result = JsonSerializer.Deserialize<AriConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (result == null)
        {
            Common.Logger.LogCritical("Failed to deserialise AriConfig.json.");
            throw new Exception("Failed to deserialise AriConfig.json.");
        }
        return result;
    }
}

public class LlmConfig
{
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

public class ModulesConfig
{
    public bool Discord { get; init; }
}