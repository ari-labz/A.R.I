using System.Text;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    /// <summary>Fires when a thread's conversation reaches the memory limit. Engram listens to this.</summary>
    internal event Action<string>? ThreadBufferFull;

    internal Dialogue(ModelConfig config) : base(config) { }

    internal Task<string> SendPrompt(
        string  threadKey,
        string  prompt,
        string  username,
        string? contextNote    = null,
        string? recallBlock    = null,
        string? contextSummary = null)
    {
        string? augmented = BuildAugmentedPrompt(prompt, contextSummary, recallBlock);
        return PromptThread(
            threadKey,
            prompt:         prompt,
            username:       username,
            augmentedPrompt: augmented,
            contextNote:    contextNote,
            recallNotes:    recallBlock,
            contextSummary: contextSummary);
    }

    private static string? BuildAugmentedPrompt(string prompt, string? contextSummary, string? recallBlock)
    {
        if (string.IsNullOrWhiteSpace(contextSummary) && string.IsNullOrWhiteSpace(recallBlock))
            return null;

        const string divider = "-------------------";
        StringBuilder sb = new();

        if (!string.IsNullOrWhiteSpace(contextSummary))
        {
            sb.AppendLine("[context]");
            sb.AppendLine(contextSummary.Trim());
            sb.AppendLine(divider);
        }

        if (!string.IsNullOrWhiteSpace(recallBlock))
        {
            sb.AppendLine("[ARI's Memories]");
            sb.AppendLine(recallBlock.Trim());
            sb.AppendLine(divider);
        }

        sb.AppendLine("[Prompt]");
        sb.Append(prompt);
        return sb.ToString();
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        thread.BufferFull += () => ThreadBufferFull?.Invoke(threadKey);
    }
}
