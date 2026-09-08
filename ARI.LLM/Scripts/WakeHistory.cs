using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// Short-term record of what recent dreams have already woken the owner about. Purely a same-week
/// dedup aid — not a durable record. A hard "don't bring this up" belongs in the Brain as a normal
/// memory note (Engram's job), not here; this only stops a *different* dream from independently
/// re-raising something a *recent* one already said, before anyone's had a chance to reply.
/// Bounded by both count and age, so it never grows into something that needs its own maintenance.
/// </summary>
internal static class WakeHistory
{
    private const int    MAX_ENTRIES = 20;
    private const int    MAX_AGE_DAYS = 14;
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(Paths.PersistentData, "WakeHistory.json");

    private sealed record Entry(DateTime Timestamp, string Topic, string Title);

    internal static void Record(string topic, string title)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        lock (Lock)
        {
            List<Entry> entries = Load();
            entries.Add(new Entry(DateTime.Now, topic.Trim(), title.Trim()));
            Save(Prune(entries));
        }
    }

    /// <summary>Short text block for injection into the dream's context. Empty string if there's
    /// nothing recent enough to matter.</summary>
    internal static string Digest()
    {
        List<Entry> entries;
        lock (Lock) { entries = Prune(Load()); }
        if (entries.Count == 0) return "";

        IEnumerable<string> lines = entries
            .OrderByDescending(e => e.Timestamp)
            .Select(e => $"- \"{e.Topic}\" ({DaysAgo(e.Timestamp)})");

        return "Topics you've already woken your owner about recently — don't re-raise any of these " +
               "in a new guise unless something genuinely new has happened since:\n" + string.Join("\n", lines);
    }

    private static string DaysAgo(DateTime ts)
    {
        TimeSpan age = DateTime.Now - ts;
        if (age.TotalHours < 1)  return "less than an hour ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static List<Entry> Prune(List<Entry> entries)
    {
        DateTime cutoff = DateTime.Now.AddDays(-MAX_AGE_DAYS);
        return entries
            .Where(e => e.Timestamp >= cutoff)
            .OrderByDescending(e => e.Timestamp)
            .Take(MAX_ENTRIES)
            .ToList();
    }

    private static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch { return []; }   // corrupt/missing file — start fresh rather than block dreaming
    }

    private static void Save(List<Entry> entries)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(entries)); }
        catch { /* non-fatal — losing dedup history is not worth failing a dream over */ }
    }
}
