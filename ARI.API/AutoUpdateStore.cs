using ARI.Common;
using System.Text.Json;

namespace ARI.API;

/// <summary>
/// The "Update automatically" setting and what the last automatic update did, kept in AutoUpdate.json in the data
/// folder. Off by default. ARI.Core's AutoUpdate reads it each hour, or straight away after "Check now"; see Documentation/Installer/Auto-Update.md.
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

    private static readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>True while ARI.Core's updater is checking, downloading or waiting for a quiet moment.</summary>
    public static volatile bool Busy;

    /// <summary>"Check now": wakes the updater so it checks straight away instead of at its next hourly check. False when a
    /// request is already waiting.</summary>
    public static bool RequestCheck()
    {
        try { wake.Release(); return true; }
        catch (SemaphoreFullException) { return false; }
    }

    /// <summary>Waits until <paramref name="max"/> has passed or a check is requested, whichever comes first.</summary>
    public static Task WaitForNextCheck(TimeSpan max, CancellationToken ct) => wake.WaitAsync(max, ct);

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
