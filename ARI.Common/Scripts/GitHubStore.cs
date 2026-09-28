using System.Text.Json;

namespace ARI.Common;

/// <summary>GitHub connection settings as configured from the control panel via the OAuth device-authorization
/// flow (see GitHubDeviceAuth). Nobody ever types or pastes a token — a device-flow run writes it here once
/// approved, and disconnecting clears it again.</summary>
public sealed class GitHubSettings
{
    /// <summary>The connected OAuth App's Client ID. Not secret — safe to keep saved and reuse across reconnects.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth access token. Secret — never returned to the browser; the API reports only whether an account is connected.</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Cached GitHub login for display in the control panel only — never used for auth.</summary>
    public string Login { get; set; } = "";
}

/// <summary>
/// Persists the GitHub connection to AppDataRoot/Server/GitHub.json. The file holds the access token in
/// plain text, so it is written 0600 (owner-only), same as Discord.json. Read on demand — connecting or
/// disconnecting takes effect immediately, since the github/git tools resolve the token fresh on every
/// call rather than caching it at startup.
/// </summary>
public static class GitHubStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "GitHub.json");
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static GitHubSettings Get()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new GitHubSettings();
                return JsonSerializer.Deserialize<GitHubSettings>(File.ReadAllText(FilePath), Options) ?? new GitHubSettings();
            }
            catch { return new GitHubSettings(); }
        }
    }

    public static void Set(GitHubSettings settings)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            Protect();
        }
    }

    /// <summary>Clears the connection but keeps the Client ID — that's app registration, not a per-connection secret.</summary>
    public static void Disconnect()
    {
        GitHubSettings s = Get();
        s.AccessToken = "";
        s.Login = "";
        Set(s);
    }

    /// <summary>The token to authenticate GitHub calls with, or null to call unauthenticated (public repos only).</summary>
    public static string? ResolveToken() => Get().AccessToken is { Length: > 0 } t ? t : null;

    // Owner-only: the access token is in here. No-op on Windows, where the file inherits the user's ACL.
    private static void Protect()
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }
}
