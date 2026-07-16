using ARI.Common;

namespace ARI.API;

/// <summary>
/// Persists the global coding-conventions rulebook under AppDataRoot/Server.
/// Edited from the control panel (api/cp/conventions) and read by the Code agent at the start of
/// every Code thread. Single source of truth — there is no per-machine renderer copy.
/// </summary>
public static class ConventionsStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "coding_conventions.md");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; }
            catch { return ""; }
        }
    }

    public static void Set(string? text)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text ?? "");
        }
    }
}
