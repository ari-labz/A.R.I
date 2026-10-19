using System.Text.Json.Serialization;

namespace ARI.Discord;

public class DiscordConfig
{
    public bool Enabled { get; init; }
    public string Token { get; init; }
    /// <summary>The Master's Discord user ID.</summary>
    public ulong MasterDiscordId { get; init; }

    /// <summary>Reads the key this setting had before it was renamed, so an older AriConfig.json still loads. Never written back.</summary>
    [JsonPropertyName("OwnerId")]
    public ulong LegacyOwnerId { init { if (value != 0 && MasterDiscordId == 0) MasterDiscordId = value; } }
    public List<ulong> WhitelistedUserIds { get; init; }
    public List<ulong> WatchedChannelIds { get; init; } = [];
    public List<ulong> AllowedGuildIds { get; init; } = [];
}
