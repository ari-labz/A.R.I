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
    public Modules modules { get; init; }

    private static readonly Regex PlaceholderPattern = new(@"\$\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    // AriConfig.json is user data (instance identity, whitelists, ports) — it lives in AppData so
    // customization survives a rebuild/update. The copy shipped with the build (BuildPath) is only
    // ever a template: the first time Paths.AriConfig is missing, seed it from that template, then
    // always read/write the AppData copy from then on.
    public static AriConfig Load()
    {
        if (!File.Exists(Paths.AriConfig))
        {
            string bundledDefault = Path.Combine(Paths.BuildPath, "AriConfig.json");
            if (!File.Exists(bundledDefault))
            {
                Shared.Logger.LogCritical($"No AriConfig.json found at {Paths.AriConfig}, and no bundled default at {bundledDefault}.");
                throw new Exception($"No AriConfig.json found at {Paths.AriConfig}, and no bundled default at {bundledDefault}.");
            }
            File.Copy(bundledDefault, Paths.AriConfig);
        }

        string json = File.ReadAllText(Paths.AriConfig);
        json = SubstitutePlaceholders(json);

        AriConfig result = JsonSerializer.Deserialize<AriConfig>(json, ReadOptions);
        if (result == null)
        {
            Shared.Logger.LogCritical("Failed to deserialise AriConfig.json.");
            throw new Exception("Failed to deserialise AriConfig.json.");
        }

        return result;
    }

    private static string SubstitutePlaceholders(string json)
    {
        Dictionary<string, string> secrets = LoadSecretsEnv();

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

    private static Dictionary<string, string> LoadSecretsEnv()
    {
        // ARI_SECRETS_PATH is an explicit override; otherwise secrets.env always lives at
        // Paths.Secrets (AppData/Server) — never in BuildPath, which may be wiped on update.
        string path = Environment.GetEnvironmentVariable("ARI_SECRETS_PATH") is { Length: > 0 } overridePath
            ? overridePath
            : Paths.Secrets;

        Dictionary<string, string> secrets = new();
        if (!File.Exists(path))
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


