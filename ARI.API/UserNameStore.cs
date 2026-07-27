using ARI.Common;

namespace ARI.API;

/// <summary>Persists the user's display name. Used by the server as the username when no auth claim is present.</summary>
public static class UserNameStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "username.txt");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : ""; }
            catch { return ""; }
        }
    }

    public static void Set(string? name)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, name?.Trim() ?? "");
        }
    }
}
