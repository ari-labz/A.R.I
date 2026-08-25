using System.Text.Json;

namespace ARI.Common;

/// <summary>GitHub tokens ARI has been given, persisted to AppData/Server/GitHub.json. A token can be
/// scoped to one project (keyed by its root path) or to the whole user; a read falls back from the
/// project token to the user token. The file holds tokens in plain text, so it is written owner-only —
/// it lives in app data, never in a project folder or the repo.</summary>
public sealed class GitHubSettings
{
    public string UserToken { get; set; } = "";
    public Dictionary<string, string> ProjectTokens { get; set; } = new();
}

public static class GitHubStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "GitHub.json");
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static GitHubSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new GitHubSettings();
            return JsonSerializer.Deserialize<GitHubSettings>(File.ReadAllText(FilePath)) ?? new GitHubSettings();
        }
        catch { return new GitHubSettings(); }
    }

    private static void Save(GitHubSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    /// <summary>The token to use for a call: this project's token if it has one, else the user token,
    /// else null (call unauthenticated).</summary>
    public static string? Resolve(string? projectRoot)
    {
        lock (Lock)
        {
            GitHubSettings s = Load();
            if (projectRoot is { Length: > 0 } && s.ProjectTokens.TryGetValue(projectRoot, out string? t) && t.Length > 0)
                return t;
            return s.UserToken.Length > 0 ? s.UserToken : null;
        }
    }

    public static void SetUserToken(string token)
    {
        lock (Lock)
        {
            GitHubSettings s = Load();
            s.UserToken = token;
            Save(s);
        }
    }

    public static void SetProjectToken(string projectRoot, string token)
    {
        lock (Lock)
        {
            GitHubSettings s = Load();
            s.ProjectTokens[projectRoot] = token;
            Save(s);
        }
    }
}
