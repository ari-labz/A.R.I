namespace ARI.LLM;

internal class Code : Agent
{
    private readonly int shortTermLimit;
    private readonly int contextTokenLimit;

    internal override int  MemoryLimit      => shortTermLimit;
    internal override int  MaxContextTokens => contextTokenLimit;
    internal override bool SuppressPromptLog => true;

    internal override ThreadType Type => ThreadType.Code;

    internal Code(AgentConfig config) : base(config)
    {
        shortTermLimit    = config.ShortTermMemoryLimit;
        contextTokenLimit = config.MaxContextTokens;
    }

    internal async Task<string> SendPrompt(
        string              threadKey,
        string              prompt,
        string              username,
        string?             platformContext    = null,
        CancellationToken   ct                 = default,
        bool                userMessagePreadded = false,
        Func<string, Task>? onDelta            = null)
    {
        return await Prompt(
            threadKey,
            prompt:             prompt,
            username:           username,
            augmentedPrompt:    null,
            platformContext:    platformContext,
            recallNotes:        null,
            contextSummary:     null,
            ct:                 ct,
            userMessagePreadded: userMessagePreadded,
            onDelta:            onDelta);
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);
        // Code threads have no Engram step — delete directly when they go inactive
        thread.BecameInactive += () => thread.MarkEngramProcessed();
    }

    internal void LogCommand(string threadKey, string input, string response)
    {
        if (Threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new CommandExchange { Input = input, Response = response, Timestamp = DateTime.Now });
    }
}
