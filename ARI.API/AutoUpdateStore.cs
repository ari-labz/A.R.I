using ARI.Common;
using System.Text.Json;

namespace ARI.API;

/// <summary>
/// The "Update automatically" setting and what the last automatic update did, kept in AutoUpdate.json in the data
/// folder. Off by default. ARI.Core's AutoUpdate reads it each hour; see Documentation/Installer/Auto-Update.md.
/// </summary>
public static class AutoUpdateStore
{
    public sealed class State
    {
        public bool    Enabled     { get; set; }
        public string? LastResult  { get; set; }   // "Handed 0.9.8 → 0.9.9 to the Installer", "No Installer found", …
        public DateTime? LastAt    { get; set; }
        public string? PendingFrom { get; set; }   // the version that shut down to update; cleared on the next start
    }

    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "AutoUpdate.json");
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static State Get()
    {
        lock (Lock)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { return new(); }
        }
    }

    public static State Update(Action<State> change)
    {
        lock (Lock)
        {
            State s = Get();
            change(s);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
            return s;
        }
    }
}
