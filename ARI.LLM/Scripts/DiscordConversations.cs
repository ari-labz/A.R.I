using System.Collections.Concurrent;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// DM conversations ARI started with discord_dm_user. Each is its own thread with a brief from the thread that
/// opened it; while one is open, every DM from that person goes into it, and ARI talks it through there and sends
/// the result back to the opener with report_to_parent (see <see cref="ParentReports"/>). It ends when she closes
/// it, or when it sits idle past <see cref="IdleTimeout"/>. The Discord module routes incoming DMs here.
/// </summary>
public static class DiscordConversations
{
    /// <summary>An open conversation closes itself after this long without activity, so a forgotten one can't
    /// swallow the person's later DMs.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public sealed class Conversation
    {
        public required ulong  UserId    { get; init; }
        public required string ThreadKey { get; init; }
        public DateTime        LastActivity { get; internal set; } = DateTime.Now;
        /// <summary>The thread that last messaged this person through discord_dm_user, which gets the reports.</summary>
        internal string?       ParentKey;
    }

    private static readonly ConcurrentDictionary<ulong, Conversation> open = new();

    /// <summary>The open conversation with this person, if any.</summary>
    public static Conversation? OpenFor(ulong userId) => open.TryGetValue(userId, out Conversation? c) ? c : null;

    /// <summary>The open conversation with this person, or a new one with its own thread key.</summary>
    internal static (Conversation Conversation, bool IsNew) GetOrOpen(ulong userId)
    {
        bool created = false;
        Conversation c = open.GetOrAdd(userId, id =>
        {
            created = true;
            return new Conversation { UserId = id, ThreadKey = $"dm:{id}:{DateTime.Now:yyyyMMddHHmmss}" };
        });
        c.LastActivity = DateTime.Now;
        return (c, created);
    }

    /// <summary>The open conversation whose thread this is, if any.</summary>
    internal static Conversation? ForThread(string threadKey) => open.Values.FirstOrDefault(c => c.ThreadKey == threadKey);

    public static void Touch(ulong userId)
    {
        if (OpenFor(userId) is { } c) c.LastActivity = DateTime.Now;
    }

    /// <summary>Closes the conversation; the person's next DM starts a new thread. Returns its thread key.</summary>
    public static string? Close(ulong userId) => open.TryRemove(userId, out Conversation? c) ? c.ThreadKey : null;

    /// <summary>Closes a conversation that went idle and tells the thread that opened it. Returns its thread key.</summary>
    public static string? CloseIdle(ulong userId)
    {
        if (!open.TryRemove(userId, out Conversation? c)) return null;
        if (c.ParentKey is { } parent)
        {
            string name = Modules.Discord?.GetUserName(userId) ?? $"Discord user {userId}";
            ParentReports.Send(parent, c.ThreadKey,
                $"[Your DM conversation with {name} closed after {IdleTimeout.TotalMinutes:0} minutes without a result.]");
        }
        return c.ThreadKey;
    }

    /// <summary>Conversations idle past <see cref="IdleTimeout"/>.</summary>
    public static IReadOnlyList<ulong> Idle() =>
        open.Values.Where(c => DateTime.Now - c.LastActivity > IdleTimeout).Select(c => c.UserId).ToList();
}
