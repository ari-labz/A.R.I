using System.Text.Json;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Records when each note was last chosen as a refactor seed, in a sidecar JSON beside the other
/// persistent state — NOT in note frontmatter, so a refactor pass never dirties the vault or adds
/// git noise for a no-op epoch. Refactor's seed ordering reads this so it visits the longest-unseen
/// notes first (never-refactored notes rank ahead of any date) instead of chewing the same
/// high-degree hubs on every run.
/// </summary>
internal sealed class RefactorLog
{
    private readonly string path;
    private readonly Dictionary<string, DateTime> stamps;

    internal RefactorLog(string persistentDir)
    {
        path   = Path.Combine(persistentDir, "RefactorLog.json");
        stamps = Load();
    }

    /// <summary>When this note was last refactored, or null if it never has been.</summary>
    internal DateTime? LastRefactored(string title) =>
        stamps.TryGetValue(title, out DateTime when) ? when : null;

    /// <summary>Sort key: a never-refactored note sorts before any dated one (oldest first).</summary>
    internal DateTime SortKey(string title) =>
        stamps.TryGetValue(title, out DateTime when) ? when : DateTime.MinValue;

    /// <summary>Mark a note as refactored now and persist. Called when its epoch ends, for every
    /// outcome (committed or no-change) so a clean note isn't re-picked next run.</summary>
    internal void Touch(string title)
    {
        stamps[title] = DateTime.UtcNow;
        Save();
    }

    private Dictionary<string, DateTime> Load()
    {
        try
        {
            if (File.Exists(path))
                return new Dictionary<string, DateTime>(
                    JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Shared.Logger.LogWarning("[Refactor] Could not read RefactorLog: {Msg}", ex.Message); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(stamps, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Shared.Logger.LogWarning("[Refactor] Could not write RefactorLog: {Msg}", ex.Message); }
    }
}
