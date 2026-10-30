using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Prompts shared across agents: the
/// ToolSystem block (list_tools/request_tools). Loaded once from Agents.json at startup so the
/// control panel edits the same text the model receives. No built-in fallbacks — a missing entry
/// is a broken config and logs an error rather than silently substituting.
/// </summary>
internal static class SharedPrompts
{
    private static Dictionary<string, string> _toolSystem = new();

    internal static void Load(Dictionary<string, string>? toolSystem)
    {
        _toolSystem = toolSystem ?? new();
    }

    private static string Get(Dictionary<string, string> src, string section, string key)
    {
        if (src.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v)) return v;
        Shared.Logger.LogError("[Prompts] Shared.{Section}.{Key} is missing from Agents.json — the model will " +
                               "receive nothing in its place.", section, key);
        return "";
    }

    private static string Sub(string text, (string Token, string Value)[] tokens)
    {
        foreach ((string token, string value) in tokens)
            text = text.Replace($"{{{token}}}", value);
        return text;
    }

    // ── ToolSystem ───────────────────────────────────────────────────────────

    internal static string ToolSystemBlock => Get(_toolSystem, "ToolSystem", "Block");
}
