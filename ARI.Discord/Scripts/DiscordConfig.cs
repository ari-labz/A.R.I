using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.Discord;

public class DiscordConfig
{
    public string Token { get; init; }
    public ulong OwnerId { get; init; }
    public List<ulong> WhitelistedUserIds { get; init; }
    public List<ulong> WatchedChannelIds { get; init; } = [];
    public List<ulong> AllowedGuildIds { get; init; } = [];

    [JsonIgnore]
    private string? filePath;

    public static DiscordConfig LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            Common.Logger.LogCritical($"DiscordConfig.json not found at {path}");
            throw new Exception($"DiscordConfig.json not found at {path}");
        }

        Common.Logger.LogInformation($"DiscordConfig.json found at {path}");

        string json = File.ReadAllText(path);
        DiscordConfig result = JsonSerializer.Deserialize<DiscordConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (result == null)
        {
            Common.Logger.LogCritical("Failed to deserialise DiscordConfig.json.");
            throw new Exception("Failed to deserialise DiscordConfig.json.");
        }

        result.filePath = path;
        return result;
    }

    public void Save()
    {
        if (filePath is null)
            throw new InvalidOperationException("Cannot save DiscordConfig: file path is unknown.");

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }
}