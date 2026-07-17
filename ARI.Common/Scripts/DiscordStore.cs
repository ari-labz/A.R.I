using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.Common;

/// <summary>Discord settings as edited from the control panel.</summary>
public sealed class DiscordSettings
{
    public bool Enabled { get; set; }

    /// <summary>Bot token. Secret — never returned to the browser; the API reports only whether one is set.</summary>
    public string Token { get; set; } = "";

    public ulong OwnerId { get; set; }
    public List<ulong> WhitelistedUserIds { get; set; } = [];
    public List<ulong> WatchedChannelIds { get; set; } = [];
    public List<ulong> AllowedGuildIds { get; set; } = [];
}

/// <summary>
/// Persists Discord settings to AppDataRoot/Server/Discord.json — the single source of truth,
/// replacing the AriConfig.json Discord section and the DISCORD_TOKEN entry in secrets.env.
///
/// The file holds the bot token in plain text, so it is written 0600 (owner-only) like secrets.env
/// was. It lives in app data and is never in the repo. Read at startup: edits need a restart.
/// </summary>
public static class DiscordStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "Discord.json");
    private static readonly object Lock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static DiscordSettings Get()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new DiscordSettings();
                return JsonSerializer.Deserialize<DiscordSettings>(File.ReadAllText(FilePath), Options)
                       ?? new DiscordSettings();
            }
            catch { return new DiscordSettings(); }
        }
    }

    public static void Set(DiscordSettings settings)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            Protect();
        }
    }

    /// <summary>True once a Discord.json exists — used to decide whether to seed it from AriConfig.</summary>
    public static bool Exists() => File.Exists(FilePath);

    // Owner-only: the token is in here. No-op on Windows, where the file inherits the user's ACL.
    private static void Protect()
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }
}
