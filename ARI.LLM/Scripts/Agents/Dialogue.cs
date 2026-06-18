using System.Text;
using System.Text.Json.Serialization;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    [JsonPropertyName("shortTermMemoryLimit")] public int ShortTermMemoryLimit { get; init; }
    [JsonPropertyName("maxImageTokens")]       public int MaxImageTokens        { get; init; }

    private readonly Context? context;

    internal override int  MemoryLimit      => ShortTermMemoryLimit;
    internal override bool SuppressPromptLog => true;

    internal override ThreadType Type => ThreadType.Dialogue;

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal Dialogue(Context? context = null)
    {
        this.context = context;
    }

    internal void NotifyTyping(string threadKey) => GetThread(threadKey)?.ResetInactivityTimer();

    internal async Task<string> SendPrompt(
        string threadKey,
        string prompt,
        string username,
        string? augmentedPrompt = null,
        string? platformContext = null,
        string? recallNotes = null,
        string? contextSummary = null,
        CancellationToken ct = default,
        bool userMessagePreadded = false,
        Func<string, Task>? onDelta = null)
    {
        return await SendPrompt(
            threadKey,
            prompt: prompt,
            username: username,
            augmentedPrompt: augmentedPrompt,
            platformContext: platformContext,
            recallNotes: recallNotes,
            contextSummary: contextSummary,
            ct: ct,
            userMessagePreadded: userMessagePreadded,
            onDelta: onDelta);
    }

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);
        thread.BufferFull += () => ThreadBufferFull?.Invoke(threadKey);
        thread.BecameInactive += () => ThreadBecameInactive?.Invoke(threadKey);

        if (context is not null)
            thread.ExchangeCompleted += (user, asst) => _ = context.Update(threadKey, user, asst);
    }
}
