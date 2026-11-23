using System.Collections.Concurrent;
using ARI.BrainVault;

namespace ARI.LLM;

/// <summary>
/// Mood neighbourhoods, the soft way a brain does it: no folders, just anchors that light up. A keyword starting with # on a note is a mood
/// anchor (#intimate, #work, #late-night). Reading a note in a conversation lights its anchors there for a while; searches in that conversation
/// then bring up notes sharing a lit anchor first and push notes whose moods aren't lit further down. Nothing is hidden, so the same note is
/// reachable from any mood, and the sensitive flag still decides who may hear it.
/// </summary>
internal static class MoodAnchors
{
    private static readonly TimeSpan Fade = TimeSpan.FromMinutes(45);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> lit = new();

    /// <summary>The conversation a thread belongs to: the memory agent's thread for a chat lights the chat's moods.</summary>
    private static string Conversation(string key) => key.EndsWith(":memory", StringComparison.Ordinal) ? key[..^":memory".Length] : key;

    internal static IEnumerable<string> Of(Note note) => note.Keywords.Where(k => k.StartsWith('#') && k.Length > 1).Select(k => k.ToLowerInvariant());

    internal static void Light(string threadKey, Note note)
    {
        ConcurrentDictionary<string, DateTime> moods = lit.GetOrAdd(Conversation(threadKey), _ => new());
        foreach (string a in Of(note)) moods[a] = DateTime.UtcNow;
    }

    internal static HashSet<string> Lit(string threadKey) =>
        lit.TryGetValue(Conversation(threadKey), out var moods)
            ? moods.Where(m => DateTime.UtcNow - m.Value < Fade).Select(m => m.Key).ToHashSet()
            : new();

    /// <summary>Search hits in this conversation's mood order: sharing a lit anchor first, no anchors next, moods that aren't lit last. Stable otherwise.</summary>
    internal static List<SearchResult> Rank(string threadKey, List<SearchResult> hits)
    {
        HashSet<string> now = Lit(threadKey);
        return hits.Select((h, i) => (h, i, anchors: Of(h.Note).ToList()))
            .OrderBy(x => x.anchors.Count == 0 ? 1 : x.anchors.Any(now.Contains) ? 0 : 2)
            .ThenBy(x => x.i)
            .Select(x => x.h).ToList();
    }
}
