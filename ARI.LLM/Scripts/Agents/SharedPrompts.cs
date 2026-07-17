using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Prompts that are not owned by any single agent: the MemoryAgent block that Engram/Refactor/Curiosity
/// share, and the [Budgets] footer appended to every agent. Loaded once from Agents.json at startup
/// (the "Shared" object), so the control panel edits the same text the model receives.
///
/// There are deliberately no built-in fallbacks — #182: the shipped Agents.json is the one source of
/// prompt text. A missing entry is a broken config, and says so in the log rather than quietly
/// substituting a different prompt than the one on screen.
/// </summary>
internal static class SharedPrompts
{
    private static Dictionary<string, string> _memory  = new();
    private static Dictionary<string, string> _budgets = new();

    internal static void Load(Dictionary<string, string>? memoryAgent, Dictionary<string, string>? budgets)
    {
        _memory  = memoryAgent ?? new();
        _budgets = budgets     ?? new();
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
            text = text.Replace("{" + token + "}", value);
        return text;
    }

    // ── MemoryAgent — shared by Engram, Refactor, Curiosity ──────────────────────

    /// <summary>The taxonomy/hub/dedup rulebook appended to each memory agent's system prompt.</summary>
    internal static string GraphRulebook => Get(_memory, "MemoryAgent", "GraphRulebook");

    /// <summary>The turn sent after each look at the graph. Tokens: {seedTitle} {skeleton} {task}.</summary>
    internal static string Epoch(params (string Token, string Value)[] tokens)
        => Sub(Get(_memory, "MemoryAgent", "EpochPrompt"), tokens);

    // ── Budgets — appended to every agent ────────────────────────────────────────

    /// <summary>The [Budgets] footer. Tokens: {thinkingTokens} {replyTokens} {contextTokens} {toolBudget}.
    /// A line whose token resolves to 0 is dropped by the caller.</summary>
    internal static string BudgetsBlock => Get(_budgets, "Budgets", "Block");
}
