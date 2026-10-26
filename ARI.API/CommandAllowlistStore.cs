using System.Text.Json;
using ARI.Common;

namespace ARI.API;

/// <summary>
/// The global "allow every time" list for run_command: command keys like <c>git commit</c> or <c>dotnet build</c>,
/// shared by every project and thread. The chat client decides what a key is (and refuses to store dangerous
/// forms such as <c>git reset --hard</c> — those ask every time); this just persists the list under the server's
/// persistent data so it survives restarts and follows the user across clients.
/// </summary>
public static class CommandAllowlistStore
{
    private const int MaxEntries   = 1000;
    private const int MaxKeyLength = 200;

    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(Paths.PersistentData, "command_allowlist.json");

    public static List<string> Get()
    {
        lock (Lock)
        {
            try
            {
                return File.Exists(FilePath)
                    ? Clean(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new())
                    : new();
            }
            catch { return new(); }
        }
    }

    /// <summary>Replaces the list (trimmed, de-duplicated, bounded) and returns what was stored.</summary>
    public static List<string> Set(IEnumerable<string>? commands)
    {
        List<string> clean = Clean(commands ?? Enumerable.Empty<string>());
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        return clean;
    }

    private static List<string> Clean(IEnumerable<string> commands) =>
        commands.Select(c => (c ?? "").Trim())
                .Where(c => c.Length > 0 && c.Length <= MaxKeyLength)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxEntries)
                .ToList();
}
