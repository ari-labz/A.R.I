using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.Common;

/// <summary>
/// Whether Engram may remember conversations that are not the Master's own (server channels, DMs with other people, guests' chats).
/// Off by default: ARI is private first. Stored as <c>EngramOthers.Enabled</c> in AriConfig.json and cached, since it is read on every exchange.
/// </summary>
public static class EngramOthersSetting
{
    private static readonly object gate = new();
    private static bool? cached;

    /// <summary>Whether Engram may remember conversations that are not the Master's own: server channels, DMs with other people and guests' chats.
    /// Off by default.</summary>
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
            if (obj["EngramOthers"] is not JsonObject section) obj["EngramOthers"] = section = new JsonObject();
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
            JsonNode? root = JsonNode.Parse(File.ReadAllText(Paths.AriConfig), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            return root?["EngramOthers"]?["Enabled"]?.GetValue<bool>() ?? false;
        }
        catch { return false; }
    }
}
