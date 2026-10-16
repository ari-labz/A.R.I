using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ARI.API.Setup;

/// <summary>
/// First-run state: has the setup wizard been finished, has the guided tour been finished, and the
/// one-time SetupToken that gates creating the first account (printed in the log, so only whoever can
/// read the log can claim a fresh install). Stored in Setup.json next to the other persistent configs.
/// An install that already has users when this file is first looked for is an existing install — both
/// flags are set so it never sees the wizard.
/// </summary>
public static class SetupState
{
    private static readonly object gate = new();
    private static string FilePath => Path.Combine(Paths.PersistentData, "Setup.json");

    public static bool SetupComplete    { get; private set; }
    public static bool TutorialComplete { get; private set; }
    public static string? Token         { get; private set; }

    public static void Init(ILogger log)
    {
        lock (gate)
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    JsonNode? n = JsonNode.Parse(File.ReadAllText(FilePath));
                    SetupComplete    = n?["setupComplete"]?.GetValue<bool>()    ?? false;
                    TutorialComplete = n?["tutorialComplete"]?.GetValue<bool>() ?? false;
                }
                catch { /* unreadable → treat as not done */ }
            }
            else if (HasUsers())
            {
                SetupComplete = TutorialComplete = true;
                Save();
                log.LogInformation("[Setup] Existing install detected — setup wizard skipped.");
            }
        }
    }

    /// <summary>Generates (once per boot) and logs the token while the install has no account yet.</summary>
    public static void AnnounceToken(ILogger log)
    {
        Token = $"{Chunk()}-{Chunk()}-{Chunk()}";
        log.LogWarning("""

            ═══════════════════════════════════════════════════════
              A·R·I first-time setup
              Open the web page and enter this Setup Token:

                  {Token}

            ═══════════════════════════════════════════════════════
            """, Token);
    }

    public static void ClearToken() => Token = null;

    public static bool TokenMatches(string? given)
    {
        if (Token is null || string.IsNullOrWhiteSpace(given)) return false;
        byte[] a = System.Text.Encoding.UTF8.GetBytes(Token);
        byte[] b = System.Text.Encoding.UTF8.GetBytes(given.Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static void Set(bool? setup = null, bool? tutorial = null)
    {
        lock (gate)
        {
            if (setup.HasValue)    SetupComplete    = setup.Value;
            if (tutorial.HasValue) TutorialComplete = tutorial.Value;
            Save();
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Paths.PersistentData);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(
            new { setupComplete = SetupComplete, tutorialComplete = TutorialComplete },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Chunk()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 4).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    private static bool HasUsers()
    {
        if (!File.Exists(Paths.UsersDb)) return false;
        try
        {
            using SqliteConnection c = new($"Data Source={Paths.UsersDb};Mode=ReadOnly");
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Users";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }
}
