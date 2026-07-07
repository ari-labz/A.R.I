using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// One thing Ari became curious about while reflecting on her brain — a question she'd like to ask,
/// a follow-up worth making. Produced by the brain scan, consumed by the proactive-message task.
/// </summary>
public record Curiosity(
    string Id,
    string Question,                 // what she'd actually ask
    string Topic,                    // the main entity/subject it's about
    IReadOnlyList<string> Keywords,  // for matching against conversation topics later
    string Reason,                   // why she's curious (for her own record)
    int Priority,                    // 1 (idle wondering) .. 5 (really wants to know)
    string Status,                   // "pending" | "asked"
    string Created,
    string? AskedAt);

/// <summary>
/// Reads/writes Curiosities.json in persistent data. The scheduler runs one task at a time, so no
/// locking is needed. Dedup is by normalised topic+question so a scan never piles up the same question.
/// </summary>
public static class CuriosityStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string PathFor(string dir) => Path.Combine(dir, "Curiosities.json");

    public static List<Curiosity> Load(string dir)
    {
        string path = PathFor(dir);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<Curiosity>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(string dir, List<Curiosity> list)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathFor(dir), JsonSerializer.Serialize(list, Json));
    }

    private static string Key(string topic, string question) =>
        (topic + "|" + question).ToLowerInvariant().Trim();

    /// <summary>Appends curiosities not already present (by topic+question). Returns how many were added.</summary>
    public static int AddNew(string dir, IEnumerable<Curiosity> incoming)
    {
        List<Curiosity> existing = Load(dir);
        HashSet<string> seen = existing.Select(c => Key(c.Topic, c.Question)).ToHashSet();
        int added = 0;
        foreach (Curiosity c in incoming)
        {
            if (!seen.Add(Key(c.Topic, c.Question))) continue;
            existing.Add(c);
            added++;
        }
        if (added > 0) Save(dir, existing);
        return added;
    }

    /// <summary>Removes a curiosity by id. Returns true if one was removed.</summary>
    public static bool Remove(string dir, string id)
    {
        List<Curiosity> existing = Load(dir);
        int removed = existing.RemoveAll(c => c.Id == id);
        if (removed > 0) Save(dir, existing);
        return removed > 0;
    }

    /// <summary>Pending topics, lower-cased — given to the scan so it doesn't re-raise what's already queued.</summary>
    public static HashSet<string> PendingTopics(string dir) =>
        Load(dir).Where(c => c.Status == "pending").Select(c => c.Topic.ToLowerInvariant()).ToHashSet();
}
