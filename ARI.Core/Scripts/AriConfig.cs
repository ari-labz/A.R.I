using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Brain;
using ARI.Discord;
using ARI.LLM;

namespace ARI.Core.Scripts;

public class AriConfig
{
    public TriliumConfig Trilium { get; init; }
    public DockerConfig Docker { get; init; }
    public ModulesConfig Modules { get; init; }
    public WebPanelConfig WebPanel { get; init; } = new();
    public VoiceSynthesisConfig VoiceSynthesis { get; init; } = new();
    public VoiceConfig Voice { get; init; } = new();

    // Sections absorbed from the former standalone config files.
    public AriLLMConfig      Llm     { get; init; } = new();
    public List<AgentConfig> Agents  { get; init; } = new();
    public BrainConfig?      Brain   { get; init; }
    public DiscordConfig?    Discord { get; init; }

    private string filePath = "";

    public static AriConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            Common.Logger.LogCritical($"AriConfig.json not found at {path}");
            throw new Exception($"AriConfig.json not found at {path}");
        }

        string json = File.ReadAllText(path);
        AriConfig result = JsonSerializer.Deserialize<AriConfig>(json, ReadOptions);
        if (result == null)
        {
            Common.Logger.LogCritical("Failed to deserialise AriConfig.json.");
            throw new Exception("Failed to deserialise AriConfig.json.");
        }

        result.filePath = path;
        result.Llm.Validate();

        // The Discord section persists runtime edits by rewriting the whole combined file.
        if (result.Discord is not null)
            result.Discord.OnSave = result.Save;

        return result;
    }

    /// <summary>Persists the whole config back to disk. Used for runtime changes like Discord whitelist edits.</summary>
    public void Save() => File.WriteAllText(filePath, JsonSerializer.Serialize(this, WriteOptions));

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
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
    public bool Voice { get; init; }
    public bool Client { get; init; } = true;
}

public class VoiceConfig
{
    public string ModelName { get; init; } = "Voice";
}

public class VoiceSynthesisConfig
{
    public string F5Path    { get; init; } = "";
    public string VoicesPath { get; init; } = "";
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
    /// <summary>Single allowed email (legacy). Use AllowedEmails for multi-user access.</summary>
    public string AllowedEmail { get; init; } = "";

    /// <summary>List of allowed emails. If non-empty, takes precedence over AllowedEmail.</summary>
    public List<string> AllowedEmails { get; init; } = new();

    /// <summary>Returns the effective allowlist — AllowedEmails if populated, otherwise the single AllowedEmail.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveAllowedEmails =>
        AllowedEmails.Count > 0 ? AllowedEmails : (AllowedEmail.Length > 0 ? new List<string> { AllowedEmail } : new List<string>());
}
