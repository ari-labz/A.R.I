using System.Text;
using ARI.Brain;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    private readonly BrainService? brain;

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    private readonly int shortTermMemoryLimit;
    private readonly int maxContextTokens;

    internal Dialogue(AgentConfig config, BrainService? brain = null) : base(config)
    {
        this.brain           = brain;
        shortTermMemoryLimit = config.ShortTermMemoryLimit;
        maxContextTokens     = config.MaxContextTokens;
    }

    protected override int GetShortTermMemoryLimit() => shortTermMemoryLimit;
    protected override int GetMaxContextTokens()      => maxContextTokens;

    internal async Task<string> SendPrompt(
        string            threadKey,
        string            prompt,
        string            username,
        string?           platformContext     = null,
        string?           contextSummary      = null,
        CancellationToken ct                  = default,
        bool              userMessagePreadded = false)
    {
        const string divider = "-------------------";
        StringBuilder sb = new();

        if (brain is not null)
        {
            List<string> paths = await brain.GetNotePaths();
            if (paths.Count > 0)
            {
                sb.AppendLine("[Stored memories — call search_memories with the bare title to retrieve any of these]");
                sb.AppendLine(string.Join(", ", paths));
                sb.AppendLine(divider);
            }
        }

        if (!string.IsNullOrWhiteSpace(contextSummary))
        {
            sb.AppendLine("[Context]");
            sb.AppendLine(contextSummary.Trim());
            sb.AppendLine(divider);
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
            prompt:              prompt,
            username:            username,
            augmentedPrompt:     augmented,
            platformContext:     platformContext,
            contextSummary:      contextSummary,
            ct:                  ct,
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

        if (brain is not null)
        {
            BrainService brainRef = brain;
            thread.RegisterTool("search_memories", Recall.schema, argsJson => Recall.Execute(brainRef, argsJson));
        }

        thread.BufferFull     += () => ThreadBufferFull?.Invoke(threadKey);
        thread.BecameInactive += () => ThreadBecameInactive?.Invoke(threadKey);
    }
}
