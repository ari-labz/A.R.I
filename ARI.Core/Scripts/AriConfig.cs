using System.Text.Json;

namespace ARI.Core.Scripts;

public class AriConfig
{
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }
    public ModulesConfig Modules { get; init; }
    public WebPanelConfig WebPanel { get; init; } = new();
    public VoiceSynthesisConfig VoiceSynthesis { get; init; } = new();

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
    public bool VoiceSynthesis { get; init; }
}

public class VoiceSynthesisConfig
{
    public string RvcPath { get; init; } = "";
    public int Port { get; init; } = 7860;
}

public class WebPanelConfig
{
    public int Port { get; init; } = 5000;
    public GoogleAuthConfig Google { get; init; } = new();
}

public class GoogleAuthConfig
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string AllowedEmail { get; init; } = "";
}


