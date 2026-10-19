using System.Text.Json.Serialization;

namespace ARI.Discord;

public class DiscordConfig
{
    public bool Enabled { get; init; }
    public string Token { get; init; }
    /// <summary>The Master's Discord user ID. Read from the original "OwnerId" key so existing AriConfig.json files still load.</summary>
    [JsonPropertyName("OwnerId")]
    public ulong MasterDiscordId { get; init; }
    public List<ulong> WhitelistedUserIds { get; init; }
    public List<ulong> WatchedChannelIds { get; init; } = [];
    public List<ulong> AllowedGuildIds { get; init; } = [];
}
