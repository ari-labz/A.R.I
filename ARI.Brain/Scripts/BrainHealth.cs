using System.Text.RegularExpressions;

namespace ARI.BrainVault;

/// <summary>What a read-only look at the graph found. Only titles and counts: no note is changed, and no note's text leaves the brain.</summary>
public sealed record BrainHealthReport(
    int Notes,
    IReadOnlyList<string> DeadEnds,
    IReadOnlyList<string> Orphans,
    IReadOnlyList<string> Untyped,
    IReadOnlyList<string> Stale,
    IReadOnlyList<(string A, string B)> DuplicateCandidates,
    int UnresolvedLinks);   // -1 when the index hasn't changed since it was last read and the count isn't to hand

/// <summary>
/// Looks for what keeps a graph from being easy to follow: notes that lead nowhere, notes nothing leads to, titles that are probably the
/// same thing, notes with no type, and notes nobody has touched for a long time. It only reports. What to do about each finding is the
/// owner's call, and any fix is made afterwards by Engram while Ari is idle.
/// </summary>
public static class BrainHealth
{
    private const int SampleLimit = 60;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(120);

    // Titles that differ only by case, punctuation, a leading article or word order are most likely one thing under two names.
    private static string Fingerprint(string title)
    {
        string t = Regex.Replace(title.ToLowerInvariant(), @"\([^)]*\)", " ");
        t = Regex.Replace(t, @"[^a-z0-9 ]", " ");
        string[] words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("the" or "a" or "an")).OrderBy(w => w, StringComparer.Ordinal).ToArray();
        return string.Join(' ', words);
    }

    public static BrainHealthReport Scan()
    {
        List<string> deadEnds = new(), orphans = new(), untyped = new(), stale = new();
        Dictionary<string, List<string>> byFingerprint = new(StringComparer.Ordinal);
        int total = 0;
        foreach (string title in Brain.GetTitles())
        {
            Note? note = Brain.GetNote(title);
            if (note is null) continue;
            total++;
            if (note.GetLinks().Count == 0) deadEnds.Add(note.Title);
            if (note.GetReferences().Count == 0) orphans.Add(note.Title);
            if (string.IsNullOrWhiteSpace(note.Type)) untyped.Add(note.Title);
            string file = Path.Combine(Brain.VaultRoot, note.Path);
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleAfter) stale.Add(note.Title);
            string key = Fingerprint(note.Title);
            if (key.Length > 0) { if (!byFingerprint.TryGetValue(key, out List<string>? group)) byFingerprint[key] = group = new(); group.Add(note.Title); }
        }

        List<(string, string)> duplicates = byFingerprint.Values.Where(g => g.Count > 1)
            .SelectMany(g => g.Skip(1).Select(other => (g[0], other))).Take(SampleLimit).ToList();
        return new BrainHealthReport(total, deadEnds.Take(SampleLimit).ToList(), orphans.Take(SampleLimit).ToList(), untyped.Take(SampleLimit).ToList(),
            stale.Take(SampleLimit).ToList(), duplicates, Brain.IndexIfChanged()?.UnresolvedLinks.Count ?? -1);
    }
}
