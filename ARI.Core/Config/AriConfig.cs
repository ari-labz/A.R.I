using ARI.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Brain;
using ARI.Discord;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using ARI.API;
using ARI.Listener;
using ARI.Scheduler;

namespace ARI.Core;

public class AriConfig
{
    public string DockerComposePath { get; init; }
    public Modules modules { get; init; }

    private static readonly Regex PlaceholderPattern = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static AriConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            Shared.Logger.LogCritical($"AriConfig.json not found at {path}");
            throw new Exception($"AriConfig.json not found at {path}");
        }

        string json = File.ReadAllText(path);
        json = SubstitutePlaceholders(json, Path.GetDirectoryName(path));

        AriConfig result = JsonSerializer.Deserialize<AriConfig>(json, ReadOptions);
        if (result == null)
        {
            Shared.Logger.LogCritical("Failed to deserialise AriConfig.json.");
            throw new Exception("Failed to deserialise AriConfig.json.");
        }

        return result;
    }

    private static string SubstitutePlaceholders(string json, string configDir)
    {
        Dictionary<string, string> secrets = LoadSecretsEnv(configDir);

        return PlaceholderPattern.Replace(json, match =>
        {
            string key = match.Groups[1].Value;
            if (secrets.TryGetValue(key, out string value))
                return value;

            string envValue = Environment.GetEnvironmentVariable(key);
            if (envValue != null)
                return envValue;

            Shared.Logger.LogWarning($"AriConfig.json references ${{{key}}} but no value was found in secrets.env or the environment.");
            return match.Value;
        });
    }

    private static Dictionary<string, string> LoadSecretsEnv(string configDir)
    {
        // secrets.env lives at repo root alongside compose.yaml. AriConfig.json is loaded from the
        // build output dir (e.g. ARI.Core/bin/Debug/net8.0/), so reach repo root the same way
        // StyleTtsPath/ScriptPath do elsewhere in this config: four levels up.
        string[] candidates =
        {
            Environment.GetEnvironmentVariable("ARI_SECRETS_PATH"),
            Path.Combine(configDir ?? "", "..", "..", "..", "..", "secrets.env"),
        };

        Dictionary<string, string> secrets = new();
        string path = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (path == null)
            return secrets;

        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                continue;

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            secrets[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return secrets;
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
    public ListenerConfig Listener { get; init; } = new();
    public SchedulerConfig Scheduler { get; init; } = new();
}


