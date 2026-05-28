using System.Text.Json;

namespace ARI.Discord;

public class DiscordConfig
{
    public string Token { get; init; }
    public List<ulong> WhitelistedUserIds { get; init; }
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
        return result;
    }
}