using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

/// <summary>A wake message a dream decided on but chose to send later (after her master wakes up, say). Kept in HeldWake.json so a
/// restart doesn't lose it. While one is waiting, no new dream starts: the night's decision is made, and she waits to say it.</summary>
internal sealed record HeldWake(string Content, string Context, string Title, string Topic, DateTime SendAt)
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.PersistentData, "HeldWake.json");

    internal static HeldWake? Current
    {
        get
        {
            lock (Gate)
            {
                try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<HeldWake>(File.ReadAllText(FilePath)) : null; }
                catch { return null; }
            }
        }
    }

    internal static void Hold(HeldWake wake)
    {
        lock (Gate) File.WriteAllText(FilePath, JsonSerializer.Serialize(wake, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Takes the held message out, so only one caller ever sends it.</summary>
    internal static HeldWake? Take()
    {
        lock (Gate)
        {
            HeldWake? held = null;
            try { if (File.Exists(FilePath)) held = JsonSerializer.Deserialize<HeldWake>(File.ReadAllText(FilePath)); } catch { }
            try { File.Delete(FilePath); } catch { }
            return held;
        }
    }
}
