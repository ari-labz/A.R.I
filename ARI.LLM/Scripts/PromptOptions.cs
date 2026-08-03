namespace ARI.LLM;

internal sealed class PromptOptions
{
    public string              Username            { get; init; } = "user";
    public string?             AugmentedPrompt     { get; init; }
    public string?             ModeNudge           { get; init; }
    public string?             RecallNotes         { get; init; }
    public string?             ContextSummary      { get; init; }
    public int                 MaxTokensOverride   { get; init; }
    public int                 ThinkingBudget      { get; init; }
    public bool                UserMessagePreadded { get; init; }
    public bool                ChatHidden          { get; init; }
    public Func<string, Task>? OnDelta             { get; init; }
    public CancellationToken   Ct                  { get; init; }
}
