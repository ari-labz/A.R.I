using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// Rings the phone when a reply has sat unread for too long. When a reply finishes and nobody is watching the thread, a clock starts;
/// if the owner opens the thread (or sends a message in it) before it runs out, nothing happens, and if not, one push goes to that
/// owner's devices. Replies that arrive while a clock is already running don't restart it, so a burst of replies is one buzz, sent
/// two minutes after the first unread one and carrying the latest. Kept apart from LLMModule so it can be tested with a short delay.
/// </summary>
internal sealed class UnreadNotifier
{
    private readonly TimeSpan delay;
    private readonly Func<string, bool> hasWatchers;
    private readonly Func<string, (string? OwnerId, string? Text)?> latest;
    private readonly Func<string, string, string?, Task> send;   // thread key, text, owner id
    private readonly ConcurrentDictionary<string, DateTime> unreadSince = new();

    /// <param name="hasWatchers">True while someone has the thread open.</param>
    /// <param name="latest">The thread's owner and its newest finished reply, or null if the thread is gone.</param>
    /// <param name="send">Delivers a push for a thread to its owner.</param>
    internal UnreadNotifier(TimeSpan delay, Func<string, bool> hasWatchers, Func<string, (string? OwnerId, string? Text)?> latest, Func<string, string, string?, Task> send)
    {
        this.delay = delay; this.hasWatchers = hasWatchers; this.latest = latest; this.send = send;
    }

    /// <summary>A reply has just finished on this thread.</summary>
    internal void ReplyFinished(string threadKey, string? ownerId)
    {
        if (ownerId is null) return;                                              // nobody to ring
        if (hasWatchers(threadKey)) { unreadSince.TryRemove(threadKey, out _); return; }   // they watched it arrive
        DateTime started = DateTime.UtcNow;
        if (!unreadSince.TryAdd(threadKey, started)) return;                      // already counting from an earlier unread reply
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            if (!unreadSince.TryGetValue(threadKey, out DateTime since) || since != started) return;   // read in the meantime
            unreadSince.TryRemove(threadKey, out _);
            if (hasWatchers(threadKey) || latest(threadKey) is not { } now || string.IsNullOrWhiteSpace(now.Text)) return;
            string snippet = Snippet(now.Text);
            if (snippet.Length > 0) await send(threadKey, snippet, now.OwnerId);   // a reply that was only tool use has nothing to say
        });
    }

    /// <summary>The owner has opened the thread, or written in it: whatever was unread isn't any more.</summary>
    internal void Read(string threadKey) => unreadSince.TryRemove(threadKey, out _);

    /// <summary>What the notification says: her words only, as plain text. Tool cards, edit windows and other display markers are
    /// dropped, and markdown is reduced to its text, so a push never shows "<!--ari-tool-done:…-->" or "**".</summary>
    internal static string Snippet(string text)
    {
        string plain = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        plain = Regex.Replace(plain, @"```[^\n]*\n?", " ");                              // code fences
        plain = Regex.Replace(plain, @"!?\[([^\]]*)\]\([^)]*\)", "$1");                    // links and pictures: keep the words
        plain = Regex.Replace(plain, @"(?m)^\s*(#{1,6}|>|[-*+]|\d+\.)\s+", "");             // headings, quotes, list bullets
        plain = Regex.Replace(plain, @"(\*\*|__|\*|`|~~)", "");                           // bold, italics, code, strikethrough
        plain = Regex.Replace(plain, @"<[^>]+>", " ");                                       // any other tag
        string flat = Regex.Replace(plain, @"\s+", " ").Trim();
        return flat.Length <= 160 ? flat : flat[..157].TrimEnd() + "…";
    }
}
