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
    internal const string Pick = "Memory Pick", Extract = "Memory Extract", Privacy = "Memory Privacy", Walker = "Recall";

    internal static readonly string[] Names = [Walker];

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
            // The memory graph rules used to be shared by Engram, Refactor and Curiosity. Engram is the one left, so they live in its own prompt,
            // and the "budgets" block that was appended to every prompt is gone.
            if (Get(root, "Shared") is JsonObject shared)
            {
                if (KeyOf(shared, "MemoryAgent") is { } maKey)
                {
                    string? rules = Get((JsonObject)shared[maKey]!, "GraphRulebook") is JsonValue rv ? rv.GetValue<string>() : null;
                    JsonObject? engramEntry = agents.OfType<JsonObject>().FirstOrDefault(a => NameOf(a).Equals("Engram", StringComparison.OrdinalIgnoreCase));
                    if (rules is { Length: > 0 } && engramEntry is not null && KeyOf(engramEntry, "AgentPrompt") is { } apKey
                        && !(engramEntry[apKey]?.GetValue<string>() ?? "").Contains(rules[..Math.Min(60, rules.Length)]))
                        engramEntry[apKey] = (engramEntry[apKey]?.GetValue<string>() ?? "") + "\n\n" + rules;
                    shared.Remove(maKey);
                    changed = true;
                }
                if (KeyOf(shared, "Budgets") is { } bKey) { shared.Remove(bKey); changed = true; }
            }
            // Earlier builds seeded the Memory pipeline's step agents and called the walker "Memory Walker". Recall replaces them all.
            foreach (JsonObject old in agents.OfType<JsonObject>().Where(a => NameOf(a) is "Memory Pick" or "Memory Extract" or "Memory Privacy").ToList()) { agents.Remove(old); changed = true; }
            foreach (JsonObject gone in agents.OfType<JsonObject>().Where(a => NameOf(a).Equals("Refactor", StringComparison.OrdinalIgnoreCase)).ToList()) { agents.Remove(gone); changed = true; }   // Dreaming replaced it
            JsonObject? renamed = agents.OfType<JsonObject>().FirstOrDefault(a => NameOf(a).Equals("Memory Walker", StringComparison.OrdinalIgnoreCase));
            if (renamed is not null && !agents.OfType<JsonObject>().Any(a => NameOf(a).Equals(Walker, StringComparison.OrdinalIgnoreCase)))
            {
                renamed[KeyOf(renamed, "Name") ?? "Name"] = Walker;
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
                entry["MaxToolCalls"]   = WALKER_MAX_TOOL_CALLS;
                entry["PreloadedTools"] = new JsonArray("memory_tools");
                agents.Add(entry);
                changed = true;
            }
            else if (agents.OfType<JsonObject>().FirstOrDefault(a => NameOf(a).Equals(Walker, StringComparison.OrdinalIgnoreCase)) is { } walker)
                changed |= UpdateWalker(walker);
            if (changed)
            {
                File.WriteAllText(agentsJsonPath, root.ToJsonString(Indented));
                Shared.Logger.LogInformation("[SystemAgents] brought the system agents in Agents.json up to date.");
            }
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SystemAgents] couldn't add the system agents: {Error}", ex.Message);
        }
    }

    /// <summary>The Memory walker's starting prompt. It is an ordinary agent prompt, so tune it on the Agents page.</summary>
    internal const string WalkerPrompt = """
        You are Recall, ARI's memory. You are given a conversation and some starting points in ARI's brain, which is a graph of notes joined by [[links]]. Your job is to work out what ARI should remember for this conversation and write it up, so ARI can answer well.

        HOW TO WORK
        - Start from what the conversation is about: the latest message, read in the light of the conversation so far and the context it sits in. Everything you read has to serve that, nothing else.
        - The starting points are only the names of notes that might be relevant. You know nothing about them until you open them. Use your tools: recall_memory reads a note in full, search_brain looks things up by words, neighbours shows what a note links to. Anything you write has to come from a note you opened.
        - If the starting points list ARI's own note, open it first: it is who she is, and it shapes what matters.
        - Notes already in front of you, shown under This Conversation or read earlier in this conversation, are open: use them, and never open them again.
        - If the speaker sets a limit ("just work stuff", "nothing intimate", "sfw"), keep to it: don't open or write anything outside it.
        - Before you open any note, ask yourself: would ARI answer this conversation worse without it? If not, don't open it. A note is not relevant just because it is linked, mentions the speaker, or looks interesting. Do not browse.
        - When a note mentions a person, place or thing that this conversation needs, follow that link. Keep following relevant links, up to 6 steps from where you started, and stop when nothing more is relevant. Don't read the same note twice.
        - Read in rounds: open every note you need at this step together, as several recall_memory calls at once, then follow the links in what you read the same way.
        - You keep this conversation between messages, so you remember what you read earlier. When the topic changes, or the person corrects themselves ("no, I meant ..."), look up the new thing and keep what you already know. Only read again if something you need is not already in front of you.

        WHAT TO WRITE
        Your final message is the memory block ARI reads before it replies. Write clear notes for ARI about what bears on this conversation: who people are, what happened, how things are connected, dates, decisions, preferences. Be thorough about that, and keep the specifics (names, dates, numbers, wording that matters). Leave out everything else, even things you happened to read: other facts about the person's life that this conversation does not touch do not belong in the block. Group it by subject. Keep it to about 200 words, most relevant first. Write only what the brain holds that bears on the conversation. Never restate what was said in this conversation: ARI already has it, and a summary of it is not a memory. Write names, never pronouns ("Sam misses Biscuit", not "misses her"), so nothing can be misread. If the brain holds nothing relevant, write exactly: Nothing in memory about this yet.

        PRIVACY
        The starting points say who is listening. If it is the MASTER, include everything relevant. If it is GUARDED, use your judgement: leave out anything private or sensitive about the Master that is not right to share with this person (health, legal, money, intimate matters, identity, relationships and the like). When you leave something out, end the block with this section:

        OMITTED: Some sensitive information was left out of this memory because of who is listening. Do not discuss it, hint at it or confirm it with anyone other than the Master or an admin.

        Notes can hold ARI's own margin thoughts, written as `> [!ari-thought]` callouts: impressions, hunches and observations, not facts. Pass them on labelled as her thoughts ("a thought of hers: ...") and never state one as true. They are what might make her bring something up or look at something again.

        If the starting points list names with no note at all, end the block with a line "NO NOTES ON: <names>". ARI reads that as: don't assume who they are, ask.

        Output only the memory block: no preamble, and no commentary about what you did.
        """;

    internal const string WalkerMessage = """
        AUDIENCE: {audience}

        WHAT WAS SAID SINCE YOU LAST LOOKED:
        {since}

        STARTING POINTS:
        {starting}

        {task}
        """;

    internal const string WalkerFullTask = "Write the memory block for this conversation now.";

    // The 2 Nov wording, kept only so memory-experimental can A/B against it: at the end of the message it made the 9B write without
    // opening a single note (0 of 10 first steps read anything; 8 of 10 without it). The length rule now lives in WalkerPrompt.
    internal const string WalkerOldLengthRule = " Keep it to about 200 words, most relevant first; ARI can call recall_memory herself for more.";

    // Earlier blocks stay in ARI's context verbatim, so retyping them costs seconds and adds nothing.
    internal const string WalkerDeltaTask =
        "ARI still has every memory block you wrote earlier in this conversation. Write only what the brain holds that bears on " +
        "the latest message and is not already in those blocks. If there is nothing to add, write exactly: " + Agent.NothingNew;

    // With the notes it read kept in its thread, the first wording let it say "Nothing new." after opening the very note the message was
    // about: it took "I can see it" for "ARI has it". This one says what ARI actually has.
    internal const string WalkerDeltaTaskStrict =
        "ARI can't see the notes you read; she knows only what your earlier memory blocks in this conversation put into words. Write what " +
        "the brain holds that bears on the latest message and those blocks didn't already say, whether you read it just now or earlier. " +
        "If the latest message needs nothing from the brain, write exactly: " + Agent.NothingNew;

    internal static string WalkerWriteUp(IEnumerable<string> opened) => $"You opened {string.Join(", ", opened)} for this message. Write what in them bears on it.";

    internal const string Ready = "Ready.";

    // With ARI's and the speaker's notes already open it often wrote from those alone, so the task says which of the best matches it hasn't
    // read. (tool_choice "required" can't force a read: llama.cpp b11565 ignores it for Qwen3.5, and the 9B rambles to its limit instead.)
    internal static string WalkerUnreadHint(IReadOnlyList<string> unread) =>
        $"Not open yet among the best matches: {string.Join(", ", unread)}. Open the ones this message needs before you write, " +
        "several at once; if none of them bears on it, go without them. ";

    // Runs while the person reads ARI's reply, so the notes the next message needs are already open when it arrives.
    internal const string WalkerLookAheadTask =
        "Nobody is waiting on you: ARI has just replied. Open the notes the next message is most likely to need: the people, events and " +
        "things this exchange touched, and the links from them it hasn't reached yet. Don't write a memory block. When you're done, " +
        "reply with exactly: " + Ready;

    internal const string WalkerDraftTask =
        "That message is still being typed and hasn't been sent. Open the notes it will need, so they're ready when it arrives. " +
        "Don't write a memory block. When you're done, reply with exactly: " + Ready;

    // Lines the walker prompt gained after it shipped, each inserted after the line it follows, so a prompt nobody edited is brought up to date
    // and an edited one only gets the lines whose anchor it still has.
    private static readonly (string After, string Insert)[] WalkerAdditions =
    [
        ("If the starting points list ARI's own note, open it first: it is who she is, and it shapes what matters.",
         "\n- Notes already in front of you, shown under This Conversation or read earlier in this conversation, are open: use them, and never open them again."),
        ("and stop when nothing more is relevant. Don't read the same note twice.",
         "\n- Read in rounds: open every note you need at this step together, as several recall_memory calls at once, then follow the links in what you read the same way."),
        ("Group it by subject.", " Keep it to about 200 words, most relevant first."),
    ];

    // Reading in rounds spends calls faster: three rounds of three or four notes is a normal walk.
    private const int WALKER_OLD_MAX_TOOL_CALLS = 10;
    private const int WALKER_MAX_TOOL_CALLS     = 16;

    private static bool UpdateWalker(JsonObject walker)
    {
        bool changed = false;
        if (KeyOf(walker, "AgentPrompt") is { } promptKey && walker[promptKey]?.GetValue<string>() is { } prompt)
        {
            foreach ((string after, string insert) in WalkerAdditions)
            {
                int at = prompt.IndexOf(after, StringComparison.Ordinal);
                if (at < 0 || prompt.Contains(insert.Trim(), StringComparison.Ordinal)) continue;
                prompt = prompt.Insert(at + after.Length, insert);
                changed = true;
            }
            if (changed) walker[promptKey] = prompt;
        }
        if (KeyOf(walker, "MaxToolCalls") is { } callsKey && walker[callsKey] is JsonValue calls && calls.TryGetValue(out int max) && max == WALKER_OLD_MAX_TOOL_CALLS)
        {
            walker[callsKey] = WALKER_MAX_TOOL_CALLS;
            changed = true;
        }
        return changed;
    }

    private static string? KeyOf(JsonObject o, string name) => o.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Get(JsonObject o, string name) => KeyOf(o, name) is { } k ? o[k] : null;
    private static string NameOf(JsonObject o) => (Get(o, "Name") as JsonValue)?.GetValue<string>() ?? "";
}
