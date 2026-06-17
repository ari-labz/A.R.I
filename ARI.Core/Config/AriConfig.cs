using ARI.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Brain;
using ARI.Discord;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using ARI.API;

namespace ARI.Core;

public class AriConfig
{
    public string DockerComposePath { get; init; }
    public Modules modules { get; init; }
    
    
    public static AriConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            Shared.Logger.LogCritical($"AriConfig.json not found at {path}");
            throw new Exception($"AriConfig.json not found at {path}");
        }

        string json = File.ReadAllText(path);
        AriConfig result = JsonSerializer.Deserialize<AriConfig>(json, ReadOptions);
        if (result == null)
        {
            Shared.Logger.LogCritical("Failed to deserialise AriConfig.json.");
            throw new Exception("Failed to deserialise AriConfig.json.");
        }

        return result;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip
    };
}

public class Modules
{
    public APIConfig API { get; init; } = new();
    public LLMConfig LLM { get; init; } = new();
    public VoiceSynthesisConfig VoiceSynthesis { get; init; } = new();
    public VoiceConfig Voice { get; init; } = new();
    public BrainConfig Brain { get; init; }
    public DiscordConfig Discord { get; init; }
}


