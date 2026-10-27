using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>Agents ARI ships for its own pipelines, as ordinary agents you can see and edit: the three model steps Memory takes
/// (pick notes, extract from a note, filter for privacy). They are made from the Memory agent's own settings and prompts the first
/// time they're missing, so they start out identical to what Memory has always done, and are never overwritten afterwards.</summary>
internal static class SystemAgents
{
    internal const string Pick = "Memory Pick", Extract = "Memory Extract", Privacy = "Memory Privacy";

    internal static readonly string[] Names = [Pick, Extract, Privacy];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Adds any missing system agent to Agents.json. Does nothing if there is no Memory agent to copy from.</summary>
    internal static void Ensure(string agentsJsonPath)
    {
        try
        {
            if (!File.Exists(agentsJsonPath)) return;
            JsonObject root = JsonNode.Parse(File.ReadAllText(agentsJsonPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
            string agentsKey = root.Select(kv => kv.Key).FirstOrDefault(k => k.Equals("Agents", StringComparison.OrdinalIgnoreCase)) ?? "Agents";
            if (root[agentsKey] is not JsonArray agents) return;

            JsonObject? memory = agents.OfType<JsonObject>().FirstOrDefault(a => NameOf(a).Equals("Memory", StringComparison.OrdinalIgnoreCase));
            if (memory is null) return;
            JsonObject templates = Get(memory, "PromptTemplates") as JsonObject ?? new JsonObject();

            bool changed = false;
            foreach ((string name, string key, string fallback) in new[] { (Pick, "HopPrompt", ""), (Extract, "ExtractPrompt", ""), (Privacy, "PrivacyFilter", Memory.PrivacyFilterFallback) })
            {
                if (agents.OfType<JsonObject>().Any(a => NameOf(a).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                JsonObject entry = (JsonObject)memory.DeepClone();
                foreach (string drop in new[] { "HopLimit", "PromptTemplates", "Name", "name" }.Where(d => Get(entry, d) is not null)) entry.Remove(KeyOf(entry, drop)!);
                entry["Name"]           = name;
                entry["Custom"]         = true;
                entry["System"]         = true;
                entry["Enabled"]        = true;
                entry["MessagePrompt"]  = (Get(templates, key) as JsonValue)?.GetValue<string>() is { Length: > 0 } t ? t : fallback;
                agents.Add(entry);
                changed = true;
            }
            if (changed)
            {
                File.WriteAllText(agentsJsonPath, root.ToJsonString(Indented));
                Shared.Logger.LogInformation("[SystemAgents] added the Memory step agents to Agents.json.");
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SystemAgents] couldn't add the system agents: {Error}", ex.Message);
        }
    }

    private static string? KeyOf(JsonObject o, string name) => o.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Get(JsonObject o, string name) => KeyOf(o, name) is { } k ? o[k] : null;
    private static string NameOf(JsonObject o) => (Get(o, "Name") as JsonValue)?.GetValue<string>() ?? "";
}
