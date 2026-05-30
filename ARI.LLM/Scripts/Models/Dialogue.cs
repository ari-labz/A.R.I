namespace ARI.LLM;

internal class Dialogue : Model
{
    internal event Action<string, IReadOnlyList<ChatMessage>>? ThreadBufferFull;
    internal event Action<string, string, string>? ThreadExchangeCompleted; // (threadKey, userMessage, assistantResponse)

    internal Dialogue(ModelConfig config) : base(config) { }

    internal Task<string> SendPrompt(string threadKey, string prompt, string? contextNote = null)
        => PromptThread(threadKey, prompt, contextNote);

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        thread.BufferFull        += history       => ThreadBufferFull?.Invoke(threadKey, history);
        thread.ExchangeCompleted += (user, asst)  => ThreadExchangeCompleted?.Invoke(threadKey, user, asst);
    }
}
