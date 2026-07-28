using System.Text;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

// Discord voice-channel tools. Available when Discord is connected (Modules.Discord != null).
// Registered via discord_tools group in ToolGroups.json.

internal sealed class DiscordListVoiceChannels : Tool
{
    internal override string Name => "discord_list_voice_channels";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_list_voice_channels",
            description = "List all Discord voice channels a given user is currently in, across all guilds ARI is a member of. Pass the username exactly as it appears in the message header.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    username = new { type = "string", description = "Discord username of the user to look up." }
                },
                required = new[] { "username" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult("Discord is not connected.");

        JsonElement el;
        try { el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return Task.FromResult("Error: invalid arguments."); }

        if (!el.TryGetProperty("username", out JsonElement unEl) || unEl.GetString() is not { } username || username.Length == 0)
            return Task.FromResult("Error: 'username' is required.");

        IReadOnlyList<VoiceChannelInfo> channels = discord.GetVoiceChannelsForUser(username);
        if (channels.Count == 0)
            return Task.FromResult("That user is not in any voice channel right now.");

        var sb = new StringBuilder();
        foreach (VoiceChannelInfo ch in channels)
            sb.AppendLine($"- #{ch.ChannelName} (channel_id: {ch.ChannelId}) in server \"{ch.GuildName}\" (guild_id: {ch.GuildId})");

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

internal sealed class DiscordJoinVoiceChannel : Tool
{
    internal override string Name => "discord_join_voice_channel";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_join_voice_channel",
            description = "Join a Discord voice channel by its channel ID. ARI will leave any voice channel she is already in within the same server.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    channel_id = new { type = "string", description = "Discord voice channel ID (snowflake) to join." }
                },
                required = new[] { "channel_id" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult("Discord is not connected.");

        JsonElement el;
        try { el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return Task.FromResult("Error: invalid arguments."); }

        if (!el.TryGetProperty("channel_id", out JsonElement idEl) || idEl.GetString() is not { } idStr
            || !ulong.TryParse(idStr, out ulong channelId))
            return Task.FromResult("Error: 'channel_id' is required and must be a Discord snowflake.");

        return discord.JoinVoiceChannelAsync(channelId);
    }
}

internal sealed class DiscordLeaveVoiceChannel : Tool
{
    internal override string Name => "discord_leave_voice_channel";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_leave_voice_channel",
            description = "Leave the voice channel ARI is currently in. Optionally restrict to a specific guild.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    guild_id = new { type = "string", description = "Optional guild ID to leave only that server's voice channel. Omit to leave all." }
                }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult("Discord is not connected.");

        ulong? guildId = null;
        try
        {
            JsonElement el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
            if (el.TryGetProperty("guild_id", out JsonElement gEl) && gEl.GetString() is { } gStr
                && ulong.TryParse(gStr, out ulong parsed))
                guildId = parsed;
        }
        catch { }

        return discord.LeaveVoiceChannelAsync(guildId);
    }
}
