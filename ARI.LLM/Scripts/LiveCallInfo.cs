namespace ARI.LLM;

/// <summary>Snapshot of an in-progress LLM call, updated each streaming chunk.</summary>
public sealed class LiveCallInfo
{
    public readonly string AgentName;
    public readonly string ThreadKey;
    public readonly int    ContextTokenLimit;
    public readonly int    ImageTokenLimit;

    // Updated in place by SendPromptCore once messages are built.
    public volatile int EstimatedInputTokens;
    public volatile int OutputTokenLimit;
    private volatile int _hadImages;
    public bool HadImages { get => _hadImages != 0; set => _hadImages = value ? 1 : 0; }

    // Written by the streaming loop; read by the stats endpoint.
    public volatile int EstimatedOutputTokens;

    internal LiveCallInfo(string agentName, string threadKey, int estimatedInputTokens, int outputLimit, int contextLimit, int imageTokenLimit = 0, bool hadImages = false)
    {
        AgentName            = agentName;
        ThreadKey            = threadKey;
        EstimatedInputTokens = estimatedInputTokens;
        OutputTokenLimit     = outputLimit;
        ContextTokenLimit    = contextLimit;
        ImageTokenLimit      = imageTokenLimit;
        HadImages            = hadImages;
    }
}
