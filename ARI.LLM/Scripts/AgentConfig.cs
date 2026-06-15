namespace ARI.LLM;

public class AgentConfig
{
    public string Name { get; init; }

    /// <summary>Name of the server (from LlamaServerConfig.Name) this agent routes to.</summary>
    public string ServerName { get; init; } = "";

    /// <summary>Resolved at boot from ServerName → server endpoint. Not read from JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Endpoint { get; internal set; } = "";
    public string SystemPrompt { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Only applies to Dialogue. Number of past messages to include in each prompt. 0 = no limit (use MaxContextTokens only).</summary>
    public int ShortTermMemoryLimit { get; init; }

    /// <summary>Maximum tokens to generate per response. -1 = unlimited.</summary>
    public int MaxTokens { get; init; } = -1;

    /// <summary>Maximum number of tool calls allowed per response. 0 = unlimited.</summary>
    public int MaxToolCalls { get; init; } = 0;

    /// <summary>
    /// Engram only. How often (in minutes) to sweep threads for new activity.
    /// 0 = disabled.
    /// </summary>
    public int SweepIntervalMinutes { get; init; } = 0;

    /// <summary>
    /// Engram and Recall. How many rounds of recursive note fetching to perform.
    /// Each round lets the model follow references found in previously fetched notes.
    /// Set to 0 to disable recursive fetching (Recall: disables Recall entirely).
    /// </summary>
    public int RecursiveBrainSearchDepth { get; init; } = 0;

    /// <summary>
    /// Recall only. Maximum number of notes to keep in the in-memory MRU cache.
    /// Set to 0 to disable caching.
    /// </summary>
    public int CacheSize { get; init; } = 0;

    /// <summary>
    /// Dialogue only. Maximum estimated tokens to keep in short-term memory after the message-count trim.
    /// If the remaining messages are still too large, oldest messages are dropped until under budget.
    /// Tokens are estimated at 4 characters per token. 0 = no token budget enforced.
    /// </summary>
    public int MaxContextTokens { get; init; } = 0;

    /// <summary>
    /// Dialogue only. Maximum estimated image tokens allowed per call.
    /// Tokens are estimated by subtracting the text-prompt estimate from total prompt tokens.
    /// 0 = unlimited.
    /// </summary>
    public int MaxImageTokens { get; init; } = 0;

    /// <summary>
    /// Whether this agent is allowed to think (extended reasoning / think tokens).
    /// When false, <|think_off|> is appended to the system prompt. Defaults to false.
    /// </summary>
    public bool Think { get; init; } = false;

    /// <summary>
    /// Maximum thinking tokens allowed per call when Think is true.
    /// 0 = no cap (model thinks as long as it wants).
    /// </summary>
    public int ThinkingBudget { get; init; } = 0;

    /// <summary>
    /// llama-server slot index to request for every call this agent makes.
    /// Null = no preference (server assigns freely).
    /// </summary>
    public int? Slot { get; init; } = null;

    /// <summary>
    /// Optional per-agent sampling temperature override.
    /// Null = use the global default in Thread.cs.
    /// </summary>
    public double? Temperature { get; init; } = null;

    /// <summary>
    /// Optional per-agent nucleus sampling (top_p) override.
    /// Null = use the global default in Thread.cs.
    /// </summary>
    public double? TopP { get; init; } = null;
}
