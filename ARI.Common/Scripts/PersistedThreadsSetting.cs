using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.Common;

/// <summary>
/// The "Persistent Threads" switch. Off by default — ARI is private first, so a conversation that expires is gone and
/// only its memories remain. On, an expired conversation is kept in <see cref="Paths.PersistedThreads"/> so it can be
/// reopened. Stored as <c>PersistedThreads.Enabled</c> in AriConfig.json and cached, since it is read on every exchange.
/// </summary>
public static class PersistedThreadsSetting
{
    private static readonly object gate = new();
    private static bool? cached;

    public static bool Enabled
    {
        get { lock (gate) return cached ??= Read(); }
    }

    public static void Set(bool enabled)
    {
        lock (gate)
        {
            JsonNode? root = File.Exists(Paths.AriConfig)
                ? JsonNode.Parse(File.ReadAllText(Paths.AriConfig), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
                : new JsonObject();
            if (root is not JsonObject obj) throw new InvalidOperationException("AriConfig.json is not a JSON object.");
            if (obj["PersistedThreads"] is not JsonObject section) obj["PersistedThreads"] = section = new JsonObject();
            section["Enabled"] = enabled;
            File.WriteAllText(Paths.AriConfig, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            cached = enabled;
        }
    }

    private static bool Read()
    {
        try
        {
            if (!File.Exists(Paths.AriConfig)) return false;
            JsonNode? root = JsonNode.Parse(File.ReadAllText(Paths.AriConfig),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return root?["PersistedThreads"]?["Enabled"]?.GetValue<bool>() ?? false;
        }
        catch { return false; }
    }
}
