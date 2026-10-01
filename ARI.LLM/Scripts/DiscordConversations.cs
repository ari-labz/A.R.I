using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// DM conversations ARI started with discord_dm_user. Each is its own thread; while one is open, every DM from
/// that person goes into it (or, when ARI is waiting for a reply, straight to her wait) until she closes it with
/// discord_close_dm, or it sits idle past <see cref="IdleTimeout"/>. The Discord module routes incoming DMs here.
/// </summary>
public static class DiscordConversations
{
    /// <summary>An open conversation with no reply pending closes itself after this long without activity, so a
    /// forgotten one can't swallow the person's later DMs.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public sealed class Conversation
    {
        public required ulong  UserId    { get; init; }
        public required string ThreadKey { get; init; }
        public DateTime        LastActivity { get; internal set; } = DateTime.Now;
        internal TaskCompletionSource<string>? Waiter;
        public bool AwaitingReply => Waiter is { Task.IsCompleted: false };
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

    /// <summary>Hands an incoming DM to a pending wait. False when nothing is waiting, so it should go to the
    /// conversation's thread as a normal message instead.</summary>
    public static bool TryDeliverReply(ulong userId, string text)
    {
        if (OpenFor(userId) is not { } c) return false;
        c.LastActivity = DateTime.Now;
        return c.Waiter?.TrySetResult(text) == true;
    }

    public static void Touch(ulong userId)
    {
        if (OpenFor(userId) is { } c) c.LastActivity = DateTime.Now;
    }

    /// <summary>Starts waiting for this person's next DM. The returned task completes with its text.</summary>
    internal static Task<string> AwaitReply(Conversation c)
    {
        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Waiter = tcs;
        return tcs.Task;
    }

    /// <summary>Stops waiting without a reply (timeout, stop, interjection).</summary>
    internal static void StopWaiting(Conversation c) => c.Waiter?.TrySetCanceled();

    /// <summary>Closes the conversation; the person's next DM starts a new thread. Returns its thread key.</summary>
    public static string? Close(ulong userId)
    {
        if (!open.TryRemove(userId, out Conversation? c)) return null;
        c.Waiter?.TrySetCanceled();
        return c.ThreadKey;
    }

    /// <summary>Conversations idle past <see cref="IdleTimeout"/> with no reply pending.</summary>
    public static IReadOnlyList<ulong> Idle() =>
        open.Values.Where(c => !c.AwaitingReply && DateTime.Now - c.LastActivity > IdleTimeout)
                   .Select(c => c.UserId).ToList();
}
