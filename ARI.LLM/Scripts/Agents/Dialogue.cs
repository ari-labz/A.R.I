using System.Text;
using ARI.Brain;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    private readonly BrainService? brain;
    private readonly int           shortTermLimit;
    private readonly int           contextTokenLimit;

    internal override int  MemoryLimit       => shortTermLimit;
    internal override int  MaxContextTokens  => contextTokenLimit;
    internal override bool SuppressPromptLog => true;

    internal override ThreadType Type => ThreadType.Dialogue;

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal Dialogue(AgentConfig config, BrainService? brain = null) : base(config)
    {
        this.brain     = brain;
        shortTermLimit    = config.ShortTermMemoryLimit;
        contextTokenLimit = config.MaxContextTokens;
    }

    internal void NotifyTyping(string threadKey) => GetThread(threadKey)?.ResetInactivityTimer();

    internal async Task<string> SendPrompt(
        string              threadKey,
        string              prompt,
        string              username,
        string?             platformContext    = null,
        string?             recallBlock        = null,
        string?             contextSummary     = null,
        CancellationToken   ct                 = default,
        bool                userMessagePreadded = false,
        Func<string, Task>? onDelta            = null)
    {
        const string DIVIDER = "-------------------";
        StringBuilder sb = new();

        if (recallBlock is not null)
        {
            if (recallBlock.Length > 0)
            {
                sb.AppendLine("[ARI's Memories]");
                sb.AppendLine(recallBlock.Trim());
            }
            else
            {
                sb.AppendLine("[ARI's Memories] No stored memories were found for this topic. Do not invent or guess information — say you don't have that information.");
            }
            sb.AppendLine(DIVIDER);
        }

        if (!string.IsNullOrWhiteSpace(contextSummary))
        {
            sb.AppendLine("[Context]");
            sb.AppendLine(contextSummary.Trim());
            sb.AppendLine(DIVIDER);
        }

        string? augmented = null;
        if (sb.Length > 0)
        {
            sb.AppendLine(prompt.Contains('\n') ? "[Prompts — answer each one in order]" : "[Prompt]");
            sb.Append(prompt);
            augmented = sb.ToString();
        }

        return await Prompt(
            threadKey,
            prompt:             prompt,
            username:           username,
            augmentedPrompt:    augmented,
            platformContext:    platformContext,
            recallNotes:        recallBlock,
            contextSummary:     contextSummary,
            ct:                 ct,
            userMessagePreadded: userMessagePreadded,
            onDelta:            onDelta);
    }

    internal void LogEngram(string threadKey, IReadOnlyList<NoteChange> changes)
    {
        if (Threads.TryGetValue(threadKey, out Thread? t))
            t.AddItem(new EngramEvent { Changes = changes, Timestamp = DateTime.Now });
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);

        if (brain is not null)
        {
            new Recall(brain).Register(thread);
        }

        thread.BufferFull     += () => ThreadBufferFull?.Invoke(threadKey);
        thread.BecameInactive += () => ThreadBecameInactive?.Invoke(threadKey);
    }
}
