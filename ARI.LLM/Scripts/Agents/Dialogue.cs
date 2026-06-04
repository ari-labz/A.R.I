using System.Text;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    /// <summary>Fires when a thread's conversation reaches the memory limit. Engram listens to this.</summary>
    internal event Action<string>? ThreadBufferFull;

    /// <summary>Fires when a thread goes inactive after the adaptive silence threshold is exceeded.</summary>
    internal event Action<string>? ThreadBecameInactive;

    private readonly int shortTermMemoryLimit;
    private readonly int maxContextTokens;

    internal Dialogue(AgentConfig config) : base(config)
    {
        shortTermMemoryLimit = config.ShortTermMemoryLimit;
        maxContextTokens     = config.MaxContextTokens;
    }

    protected override int GetShortTermMemoryLimit() => shortTermMemoryLimit;
    protected override int GetMaxContextTokens()      => maxContextTokens;

    internal Task<string> SendPrompt(
        string            threadKey,
        string            prompt,
        string            username,
        string?           platformContext     = null,
        string?           recallBlock         = null,
        string?           contextSummary      = null,
        CancellationToken ct                  = default,
        bool              userMessagePreadded = false)
    {
        string? augmented = null;

        if (!string.IsNullOrWhiteSpace(contextSummary) || !string.IsNullOrWhiteSpace(recallBlock))
        {
            const string divider = "-------------------";
            StringBuilder sb = new();

            if (!string.IsNullOrWhiteSpace(contextSummary))
            {
                sb.AppendLine("[Context]");
                sb.AppendLine(contextSummary.Trim());
                sb.AppendLine(divider);
            }

            if (!string.IsNullOrWhiteSpace(recallBlock))
            {
                sb.AppendLine("[ARI's Memories]");
                sb.AppendLine(recallBlock.Trim());
                sb.AppendLine(divider);
            }

            sb.AppendLine(prompt.Contains('\n') ? "[Prompts — answer each one in order]" : "[Prompt]");
            sb.Append(prompt);
            augmented = sb.ToString();
        }

        return Prompt(
            threadKey,
            prompt:             prompt,
            username:           username,
            augmentedPrompt:    augmented,
            platformContext:    platformContext,
            recallNotes:        recallBlock,
            contextSummary:     contextSummary,
            ct:                 ct,
            userMessagePreadded: userMessagePreadded);
    }

    internal void LogCommand(string threadKey, string input, string response)
    {
        if (threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new CommandExchange { Input = input, Response = response, Timestamp = DateTime.Now });
    }

    internal void LogEngram(string threadKey, IReadOnlyList<NoteChange> changes)
    {
        if (threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new EngramEvent { Changes = changes, Timestamp = DateTime.Now });
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);
        thread.BufferFull     += () => ThreadBufferFull?.Invoke(threadKey);
        thread.BecameInactive += () => ThreadBecameInactive?.Invoke(threadKey);
    }
}
