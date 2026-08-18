using ARI.Common;

namespace ARI.API;

public static class SafeModePromptStore
{
    private const string Default = "Please help me plan this change, but do not edit any files — use read-only operations only, then present a plan.";
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "safemode_prompt.txt");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : Default; }
            catch { return Default; }
        }
    }

    public static void Set(string? text)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text?.Trim() ?? "");
        }
    }
}
