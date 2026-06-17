using System.Text.Json.Serialization;

namespace ARI.LLM;

internal class Code : Agent
{
    [JsonPropertyName("shortTermMemoryLimit")] public int ShortTermMemoryLimit { get; init; }

    internal override int  MemoryLimit      => ShortTermMemoryLimit;
    internal override bool SuppressPromptLog => true;

    internal override ThreadType Type => ThreadType.Code;

    internal Code() { }

    internal async Task<string> SendPrompt(
        string threadKey,
        string prompt,
        string username,
        string? platformContext = null,
        CancellationToken ct = default,
        bool userMessagePreadded = false,
        Func<string, Task>? onDelta = null)
    {
        return await Prompt(
            threadKey,
            prompt: prompt,
            username: username,
            platformContext: platformContext,
            ct: ct,
            userMessagePreadded: userMessagePreadded,
            onDelta: onDelta);
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);
        thread.BecameInactive += () => thread.MarkEngramProcessed();
    }
}
