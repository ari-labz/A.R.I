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
        string? contextSummary = null,
        IReadOnlyList<ThreadAttachment>?      attachments        = null,
        IReadOnlyList<MessageAttachmentInfo>? messageAttachments = null)
    {
        string? augmented = BuildAugmentedPrompt(prompt, contextSummary, recallBlock, attachments, messageAttachments);
        return PromptThread(
            threadKey,
            prompt:             prompt,
            username:           username,
            augmentedPrompt:    augmented,
            contextNote:        contextNote,
            recallNotes:        recallBlock,
            contextSummary:     contextSummary,
            attachments:        attachments,
            messageAttachments: messageAttachments);
    }

    private static string? BuildAugmentedPrompt(
        string prompt,
        string? contextSummary,
        string? recallBlock,
        IReadOnlyList<ThreadAttachment>?      attachments,
        IReadOnlyList<MessageAttachmentInfo>? messageAttachments)
    {
        var threadTextFiles  = attachments?.Where(a => !a.IsImage).ToList();
        var messageTextFiles = messageAttachments?.Where(a => !a.IsImage).ToList();
        bool hasText = (threadTextFiles?.Count > 0) || (messageTextFiles?.Count > 0);

        if (string.IsNullOrWhiteSpace(contextSummary) && string.IsNullOrWhiteSpace(recallBlock) && !hasText)
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

        if (hasText)
        {
            sb.AppendLine("[Attached Files]");
            foreach (var a in threadTextFiles ?? [])
            {
                sb.AppendLine($"--- {a.Name} ---");
                sb.AppendLine(a.Content);
                sb.AppendLine("---");
            }
            foreach (var a in messageTextFiles ?? [])
            {
                sb.AppendLine($"--- {a.Name} ---");
                sb.AppendLine(a.Content);
                sb.AppendLine("---");
            }
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
