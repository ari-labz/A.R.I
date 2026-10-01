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

/// <summary>discord_dm_user: DM anyone by user ID. Opens a conversation (its own thread) with that person, which gets
/// all their DMs and a brief from this thread; ARI talks it through there and reports the result back here. Can hold
/// this turn until that report arrives. See <see cref="DiscordConversations"/> and <see cref="ParentReports"/>.</summary>
internal sealed class DiscordDmUser(Thread parent) : Tool
{
    private const int DefaultWaitSeconds = 300;
    private const int MaxWaitSeconds     = 1800;
    private const int TranscriptMessages = 20;
    private const int TranscriptChars    = 8000;
    private static readonly TimeSpan WaitPoll = TimeSpan.FromMilliseconds(250);
    internal const string ReportPrefix = "Report from the conversation: ";

    internal override string Name => "discord_dm_user";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "discord_dm_user",
            description = "Send a Discord DM to anyone by user ID. It opens a conversation with them that gets all their DMs; you handle it there, " +
                          "using the brief, and report the result back here. Set wait_for_reply to hold your reply open until that report arrives; " +
                          "if it comes later, it wakes this conversation. " +
                          $"Your owner's Discord user ID is {Modules.Discord?.OwnerId}.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    user_id        = new { type = "string",  description = "Their Discord user ID (snowflake)." },
                    message        = new { type = "string",  description = "What to send." },
                    brief          = new { type = "string",  description = "Everything the DM conversation needs to answer their follow-up questions on its own: who is asking, exactly what they asked and why, and what you need back." },
                    wait_for_reply = new { type = "boolean", description = "Hold until the conversation reports back, and return the report." },
                    max_seconds    = new { type = "integer", description = $"Longest to wait (default {DefaultWaitSeconds}, max {MaxWaitSeconds})." }
                },
                required = new[] { "user_id", "message", "brief" }
            }
        }
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Discord is not { } discord || Modules.Llm is not LLMModule llm) return "Discord is not connected.";

        ulong userId; string message, brief = ""; bool wait = false; int maxSeconds = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("user_id", out JsonElement idEl) || !ulong.TryParse(idEl.ToString(), out userId))
                return "[Error: 'user_id' is required and must be a Discord user ID.]";
            message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
            if (message.Trim().Length == 0) return "[Error: 'message' is required.]";
            if (root.TryGetProperty("brief", out JsonElement b)) brief = b.GetString() ?? "";
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

        // Point the conversation at this thread, and give it the brief. The owner's DM also gets this thread's recent
        // messages, so it can answer whatever they ask; anyone else gets the written brief only.
        convo.ParentKey = parent.Key;
        if (convo.ThreadKey != parent.Key)
        {
            Thread child = llm.ConversationThread(convo.ThreadKey);
            child.ReportsTo = parent.Key;
            child.Brief     = BuildBrief(brief, userId == discord.OwnerId);
            if (!child.tools.ContainsKey("report_to_parent")) new ReportToParent(child).Register(child);
        }

        string opened = isNew
            ? "Opened a new conversation with them. You'll handle their replies there and report back here."
            : "Sent in your open conversation with them, which now reports back here.";
        if (!wait) return $"Sent. {opened}";

        int seconds = maxSeconds <= 0 ? DefaultWaitSeconds : Math.Min(maxSeconds, MaxWaitSeconds);
        DateTime until = DateTime.Now.AddSeconds(seconds);
        string? stopped = null;
        while (true)
        {
            if (ParentReports.TakeFrom(parent.Key, convo.ThreadKey) is { } report) return $"{opened}\n{ReportPrefix}{report}";
            if (parent.HasInterjections)           { stopped = "Stopped waiting: a message arrived in this conversation. Read it; the report will still reach you."; break; }
            if (DateTime.Now >= until)             { stopped = $"No report within {seconds}s."; break; }
            if (parent.Ct.IsCancellationRequested) { stopped = "Your turn was stopped."; break; }
            try { await Task.Delay(WaitPoll, parent.Ct); }
            catch (OperationCanceledException) { }
        }
        return $"{opened}\n{stopped} The conversation stays open; its report will wake this conversation when it comes.";
    }

    private string BuildBrief(string brief, bool toOwner)
    {
        string where = parent.Key.StartsWith("guild:", StringComparison.OrdinalIgnoreCase) ? "a Discord server channel"
                     : parent.Key.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)    ? "another Discord DM"
                     : "a chat in the ARI app";
        StringBuilder sb = new();
        sb.AppendLine("--- **Why you're in this conversation** ---").AppendLine();
        sb.AppendLine($"You opened this DM on behalf of {where}. Talk it through here and answer their questions from what you know below. " +
                      "Once it's settled, call report_to_parent with what that conversation needs and done=true, which closes this DM; " +
                      "use done=false only if more is still to come.");
        if (brief.Trim().Length > 0) sb.AppendLine().AppendLine($"Brief: {brief.Trim()}");
        if (toOwner)
        {
            List<ThreadMessage> recent = parent.GetChatHistory(maxMessages: TranscriptMessages, maxChars: TranscriptChars);
            if (recent.Count > 0)
            {
                sb.AppendLine().AppendLine("Recent messages in that conversation:");
                foreach (ThreadMessage msg in recent)
                    sb.AppendLine($"{(msg.Role == "assistant" ? "ARI" : msg.Username)}: {msg.Content}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    internal override Func<string, string>? Display => args =>
        $"<!--ari-tool-start:discord_dm_user:{DmLabels.Name(args)}{(DmLabels.Waits(args) ? "|wait" : "")}-->";
}

/// <summary>report_to_parent: hands the result of a DM conversation back to the thread that opened it, which gets it
/// in its wait, at its next step, or as a new turn if it's idle. Registered on the conversation's thread by
/// discord_dm_user.</summary>
internal sealed class ReportToParent(Thread thread) : Tool
{
    internal override string Name => "report_to_parent";

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "report_to_parent",
            description = "Send the result of this conversation back to the conversation that opened it, which acts on it. Only report what it needs, once it's settled; keep the back-and-forth here.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    report = new { type = "string",  description = "What was decided or found out, with anything the other conversation needs to act on it." },
                    done   = new { type = "boolean", description = "Also close this DM conversation. Their next DM starts a new one." }
                },
                required = new[] { "report" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string report = ""; bool done = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("report", out JsonElement r)) report = r.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("done", out JsonElement d)) done = d.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return Task.FromResult<ToolResult>("[Error: arguments weren't valid JSON.]"); }
        if (report.Trim().Length == 0) return Task.FromResult<ToolResult>("[Error: 'report' is required.]");
        if (thread.ReportsTo is not { } parentKey || Modules.Llm is not LLMModule llm)
            return Task.FromResult<ToolResult>("[Error: this conversation has nothing to report to.]");
        if (!llm.HasThread(parentKey))
            return Task.FromResult<ToolResult>("The conversation that opened this one has ended, so there's nobody to report to.");

        DiscordConversations.Conversation? convo = DiscordConversations.ForThread(thread.Key);
        string name = convo is not null ? Modules.Discord?.GetUserName(convo.UserId) ?? "them" : "them";
        bool closing = done && convo is not null;
        ParentReports.Send(parentKey, thread.Key,
            $"[Report from your DM conversation with {name}{(closing ? "; it has closed itself" : "")}]\n{report.Trim()}");

        if (closing)
        {
            DiscordConversations.Close(convo!.UserId);
            llm.CloseThreadWhenIdle(thread.Key);
            return Task.FromResult<ToolResult>("Reported. This conversation is closed once you finish this reply.");
        }
        return Task.FromResult<ToolResult>("Reported.");
    }
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

    internal override Func<string, string>? Display => args =>
        $"<!--ari-tool-start:discord_close_dm:{DmLabels.Name(args)}-->";
}

/// <summary>Chip labels for the DM tools: the person's name rather than their raw ID.</summary>
internal static class DmLabels
{
    internal static string Name(string argsJson)
    {
        string? name = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("user_id", out JsonElement idEl) && ulong.TryParse(idEl.ToString(), out ulong id))
                name = Modules.Discord?.GetUserName(id);
        }
        catch (JsonException) { }
        name = (name ?? "Discord user").Replace("|", " ").Replace(":", " ").Replace("\n", " ").Trim();
        return ToolCallParser.EscapeLabel(name);
    }

    internal static bool Waits(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            return doc.RootElement.TryGetProperty("wait_for_reply", out JsonElement w) && w.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
}
