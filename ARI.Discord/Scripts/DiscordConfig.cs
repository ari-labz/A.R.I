namespace ARI.Discord;

public class DiscordConfig
{
    public string Token { get; init; }
    public ulong OwnerId { get; init; }
    public List<ulong> WhitelistedUserIds { get; init; }
    public List<ulong> WatchedChannelIds { get; init; } = [];
    public List<ulong> AllowedGuildIds { get; init; } = [];
}
