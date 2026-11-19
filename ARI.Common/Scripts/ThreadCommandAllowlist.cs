using System.Text.Json;

namespace ARI.Common;

/// <summary>
/// Per-thread "allow for this chat" list of code-run keys (python, bash, javascript). Scoped to one thread so
/// approving a language in one conversation never silences the prompt in another. Stored under the server's
/// persistent data alongside the global allowlist.
/// </summary>
public static class ThreadCommandAllowlist
{
    private const int MaxEntries   = 50;
    private const int MaxKeyLength = 100;

    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(Paths.PersistentData, "thread_command_allowlist.json");

    private static Dictionary<string, List<string>> Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch { return new(); }
        }
    }

    private static void Save(Dictionary<string, List<string>> data)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    public static bool Contains(string threadKey, string key)
    {
        Dictionary<string, List<string>> data = Load();
        return data.TryGetValue(threadKey, out List<string>? keys) && keys.Contains(key, StringComparer.Ordinal);
    }

    public static void Add(string threadKey, string key)
    {
        key = key.Trim();
        if (key.Length == 0 || key.Length > MaxKeyLength) return;
        lock (Lock)
        {
            Dictionary<string, List<string>> data = Load();
            if (!data.TryGetValue(threadKey, out List<string>? keys))
            {
                keys = new();
                data[threadKey] = keys;
            }
            if (!keys.Contains(key, StringComparer.Ordinal) && keys.Count < MaxEntries)
                keys.Add(key);
            Save(data);
        }
    }

    /// <summary>Removes a thread's entries (called when a thread is deleted).</summary>
    public static void RemoveThread(string threadKey)
    {
        lock (Lock)
        {
            Dictionary<string, List<string>> data = Load();
            if (data.Remove(threadKey))
                Save(data);
        }
    }
}
