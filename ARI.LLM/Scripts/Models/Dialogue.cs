using System.Text;

namespace ARI.LLM;

internal class Dialogue : Model
{
    internal event Action<string, IReadOnlyList<ChatMessage>>? ThreadBufferFull;
    internal event Action<string, string, string>? ThreadExchangeCompleted; // (threadKey, userMessage, assistantResponse)

    internal Dialogue(ModelConfig config) : base(config) { }

    internal Task<string> SendPrompt(string threadKey, string prompt, string? contextNote = null, string? recallBlock = null, string? contextSummary = null)
    {
        string augmented = BuildAugmentedPrompt(prompt, contextSummary, recallBlock);
        return PromptThread(threadKey, augmented, contextNote, originalUserMessage: prompt, recallNotes: recallBlock, contextSummary: contextSummary);
    }

    private static string BuildAugmentedPrompt(string prompt, string? contextSummary, string? recallBlock)
    {
        if (string.IsNullOrWhiteSpace(contextSummary) && string.IsNullOrWhiteSpace(recallBlock))
            return prompt;

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
        thread.BufferFull        += history       => ThreadBufferFull?.Invoke(threadKey, history);
        thread.ExchangeCompleted += (user, asst)  => ThreadExchangeCompleted?.Invoke(threadKey, user, asst);
    }
}
