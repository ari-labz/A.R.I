using System.Text.Json;

namespace ARI.Core.Scripts;

public class AriConfig
{
    public LlamaServerConfig LlamaServer { get; init; }
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }
    public ModulesConfig Modules { get; init; }
    public WebPanelConfig WebPanel { get; init; } = new();

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

public class LlamaServerConfig
{
    public string Endpoint { get; init; }
    public int Port { get; init; } = 8081;
    public string ModelsPath { get; init; }
    public string ModelFile { get; init; }
    public string MmprojFile { get; init; }
    public int ContextSize { get; init; } = 245760;
    public string DownloadBaseUrl { get; init; }
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
    public bool WebPanel { get; init; }
}

public class WebPanelConfig
{
    public int Port { get; init; } = 5000;
}
