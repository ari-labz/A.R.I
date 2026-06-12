using System.Text.Json.Serialization;

namespace ARI.Discord;

public class DiscordConfig
{
    public string Token { get; init; }
    public ulong OwnerId { get; init; }
    public List<ulong> WhitelistedUserIds { get; init; }
    public List<ulong> WatchedChannelIds { get; init; } = [];
    public List<ulong> AllowedGuildIds { get; init; } = [];

    /// <summary>
    /// Persists runtime changes (e.g. whitelist edits). Wired by the host to rewrite the
    /// combined config file, since this is now a section of AriConfig rather than its own file.
    /// </summary>
    [JsonIgnore]
    public Action? OnSave { get; set; }

    public void Save() => OnSave?.Invoke();
}
