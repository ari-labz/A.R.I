using System.Text;
using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

// Discord voice-channel tools. Available when Discord is connected (Modules.Discord != null).
// Registered via discord_tools group in ToolGroups.json.

internal sealed class DiscordListVoiceChannels : Tool
{
    internal override string     Name   => "discord_list_voice_channels";
    internal override ToolAccess Access => ToolAccess.Read;
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

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult<ToolResult>("Discord is not connected.");

        JsonElement el;
        try { el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return Task.FromResult<ToolResult>("Error: invalid arguments."); }

        if (!el.TryGetProperty("username", out JsonElement unEl) || unEl.GetString() is not { } username || username.Length == 0)
            return Task.FromResult<ToolResult>("Error: 'username' is required.");

        IReadOnlyList<VoiceChannelInfo> channels = discord.GetVoiceChannelsForUser(username);
        if (channels.Count == 0)
            return Task.FromResult<ToolResult>("That user is not in any voice channel right now.");

        StringBuilder sb = new StringBuilder();
        foreach (VoiceChannelInfo ch in channels)
            sb.AppendLine($"- #{ch.ChannelName} (channel_id: {ch.ChannelId}) in server \"{ch.GuildName}\" (guild_id: {ch.GuildId})");

        return Task.FromResult<ToolResult>(sb.ToString().TrimEnd());
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

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult<ToolResult>("Discord is not connected.");

        JsonElement el;
        try { el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return Task.FromResult<ToolResult>("Error: invalid arguments."); }

        if (!el.TryGetProperty("channel_id", out JsonElement idEl) || idEl.GetString() is not { } idStr
            || !ulong.TryParse(idStr, out ulong channelId))
            return Task.FromResult<ToolResult>("Error: 'channel_id' is required and must be a Discord snowflake.");

        return discord.JoinVoiceChannelAsync(channelId).AsToolResult();
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

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord)
            return Task.FromResult<ToolResult>("Discord is not connected.");

        ulong? guildId = null;
        try
        {
            JsonElement el = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
            if (el.TryGetProperty("guild_id", out JsonElement gEl) && gEl.GetString() is { } gStr
                && ulong.TryParse(gStr, out ulong parsed))
                guildId = parsed;
        }
        catch { }

        return discord.LeaveVoiceChannelAsync(guildId).AsToolResult();
    }
}

/// <summary>discord_dm_user: DM anyone by user ID. Opens a new conversation (its own thread) with that person, which
/// receives all their DMs until discord_close_dm; can hold this turn until they reply. See <see cref="DiscordConversations"/>.</summary>
internal sealed class DiscordDmUser(Thread parent) : Tool
{
    private const int DefaultWaitSeconds = 300;
    private const int MaxWaitSeconds     = 1800;
    private static readonly TimeSpan WaitPoll = TimeSpan.FromMilliseconds(250);

    internal override string Name => "discord_dm_user";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_dm_user",
            description = "Send a Discord DM to anyone by user ID. It opens a new conversation with them, which gets all their DMs until you close it with discord_close_dm. Set wait_for_reply to hold your reply open until they answer. " +
                          $"Your owner's Discord user ID is {Modules.Discord?.OwnerId}.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    user_id        = new { type = "string",  description = "Their Discord user ID (snowflake)." },
                    message        = new { type = "string",  description = "What to send." },
                    wait_for_reply = new { type = "boolean", description = "Hold until they reply, and return their reply." },
                    max_seconds    = new { type = "integer", description = $"Longest to wait (default {DefaultWaitSeconds}, max {MaxWaitSeconds})." }
                },
                required = new[] { "user_id", "message" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord || Modules.Llm is not LLMModule llm) return "Discord is not connected.";

        ulong userId; string message; bool wait = false; int maxSeconds = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("user_id", out JsonElement idEl) || !ulong.TryParse(idEl.ToString(), out userId))
                return "[Error: 'user_id' is required and must be a Discord user ID.]";
            message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
            if (message.Trim().Length == 0) return "[Error: 'message' is required.]";
            if (root.TryGetProperty("wait_for_reply", out JsonElement w)) wait = w.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("max_seconds", out JsonElement s) && s.TryGetInt32(out int secs)) maxSeconds = secs;
        }
        catch (JsonException) { return "[Error: arguments weren't valid JSON.]"; }

        (DiscordConversations.Conversation convo, bool isNew) = DiscordConversations.GetOrOpen(userId);
        if (await discord.SendDirectMessageAsync(userId, message) is { } failure)
        {
            if (isNew) DiscordConversations.Close(userId);
            return $"[Error: couldn't send the DM: {failure}]";
        }
        llm.RecordAriMessage(convo.ThreadKey, message);

        string opened = isNew ? "Opened a new conversation with them; their DMs go to it until you call discord_close_dm." : "Sent in your open conversation with them.";
        if (!wait) return $"Sent. {opened}";

        int seconds = maxSeconds <= 0 ? DefaultWaitSeconds : Math.Min(maxSeconds, MaxWaitSeconds);
        DateTime until = DateTime.Now.AddSeconds(seconds);
        Task<string> reply = DiscordConversations.AwaitReply(convo);
        string? stopped = null;
        while (!reply.IsCompleted)
        {
            if (parent.HasInterjections)          { stopped = "Stopped waiting: a message arrived in this conversation. Read it; you can check for their reply again by waiting later."; break; }
            if (DateTime.Now >= until)            { stopped = $"No reply within {seconds}s."; break; }
            if (parent.Ct.IsCancellationRequested) { stopped = "Your turn was stopped."; break; }
            try { await Task.WhenAny(reply, Task.Delay(WaitPoll, parent.Ct)); }
            catch (OperationCanceledException) { }
        }
        if (reply.IsCompletedSuccessfully) return $"{opened}\nTheir reply: {reply.Result}";
        DiscordConversations.StopWaiting(convo);
        return $"{opened}\n{stopped} The conversation stays open, so a later reply lands in it.";
    }

    internal override Func<string, string>? Display => args =>
        $"<!--ari-tool-start:discord_dm_user:{ToolCallParser.EscapeLabel(ToolCallParser.TryExtractJsonString(args, "user_id") ?? "user")}-->";
}

/// <summary>discord_close_dm: end the conversation discord_dm_user opened; the person's next DM starts a new thread.</summary>
internal sealed class DiscordCloseDm : Tool
{
    internal override string Name => "discord_close_dm";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_close_dm",
            description = "Close the DM conversation you opened with discord_dm_user once it's served its purpose. Their next DM starts a new conversation.",
            parameters  = new
            {
                type       = "object",
                properties = new { user_id = new { type = "string", description = "Their Discord user ID." } },
                required   = new[] { "user_id" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        ulong userId;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (!doc.RootElement.TryGetProperty("user_id", out JsonElement idEl) || !ulong.TryParse(idEl.ToString(), out userId))
                return Task.FromResult<ToolResult>("[Error: 'user_id' is required.]");
        }
        catch (JsonException) { return Task.FromResult<ToolResult>("[Error: arguments weren't valid JSON.]"); }

        if (DiscordConversations.Close(userId) is not { } threadKey)
            return Task.FromResult<ToolResult>("There's no open conversation with them.");
        if (Modules.Llm is LLMModule llm) _ = llm.CloseThreadAsync(threadKey);
        return Task.FromResult<ToolResult>("Closed. Their next DM starts a new conversation.");
    }
}
