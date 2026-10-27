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
    internal const string Pick = "Memory Pick", Extract = "Memory Extract", Privacy = "Memory Privacy", Walker = "Memory Walker";

    internal static readonly string[] Names = [Pick, Extract, Privacy, Walker];

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
            if (!agents.OfType<JsonObject>().Any(a => NameOf(a).Equals(Walker, StringComparison.OrdinalIgnoreCase)))
            {
                JsonObject entry = (JsonObject)memory.DeepClone();
                foreach (string drop in new[] { "HopLimit", "PromptTemplates", "Name", "name" }.Where(d => Get(entry, d) is not null)) entry.Remove(KeyOf(entry, drop)!);
                entry["Name"]           = Walker;
                entry["Custom"]         = true;
                entry["System"]         = true;
                entry["Enabled"]        = true;
                entry["AgentPrompt"]    = WalkerPrompt;
                entry["MessagePrompt"]  = WalkerMessage;
                entry["MaxToolCalls"]   = 30;
                entry["PreloadedTools"] = new JsonArray("memory_tools");
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

    /// <summary>The Memory walker's starting prompt. It is an ordinary agent prompt, so tune it on the Agents page.</summary>
    internal const string WalkerPrompt = """
        You are Memory, ARI's memory. You are given a conversation and some starting points in ARI's brain, which is a graph of notes joined by [[links]]. Your job is to work out what ARI should remember for this conversation and write it up, so ARI can answer well.

        HOW TO WORK
        - Read the starting points. Then use your tools to find more: search_brain looks things up by words, recall_memory reads a note in full, neighbours shows what a note links to.
        - Read the notes that matter. When a note mentions a person, place or thing that matters to this conversation, follow that link and read that note too. Keep following relevant links, up to 6 steps from where you started, and stop when nothing more is relevant.
        - Don't follow links that have nothing to do with the conversation, and don't read the same note twice.
        - You keep this conversation between messages, so you remember what you read earlier. When the topic changes, or the person corrects themselves ("no, I meant ..."), look up the new thing and keep what you already know. Only read again if something you need is not already in front of you.

        WHAT TO WRITE
        Your final message is the memory block ARI reads before it replies. Write clear notes for ARI about everything relevant: who people are, what happened, dates, decisions, preferences. Be thorough rather than terse: keep the specifics (names, dates, numbers, wording that matters) and drop only what has nothing to do with the conversation. Group it by subject. Cover the conversation so far, not only the latest message.

        PRIVACY
        The starting points say who is listening. If it is the MASTER, include everything relevant. If it is GUARDED, use your judgement: leave out anything private or sensitive about the Master that is not right to share with this person (health, legal, money, intimate matters, identity, relationships and the like). When you leave something out, end the block with this section:

        OMITTED: Some sensitive information was left out of this memory because of who is listening. Do not discuss it, hint at it or confirm it with anyone other than the Master or an admin.

        Output only the memory block: no preamble, and no commentary about what you did.
        """;

    internal const string WalkerMessage = """
        AUDIENCE: {audience}

        WHAT WAS SAID SINCE YOU LAST LOOKED:
        {since}

        STARTING POINTS:
        {starting}

        Write the memory block for this conversation now.
        """;

    private static string? KeyOf(JsonObject o, string name) => o.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Get(JsonObject o, string name) => KeyOf(o, name) is { } k ? o[k] : null;
    private static string NameOf(JsonObject o) => (Get(o, "Name") as JsonValue)?.GetValue<string>() ?? "";
}
