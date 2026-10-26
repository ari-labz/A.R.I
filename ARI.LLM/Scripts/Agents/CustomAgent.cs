namespace ARI.LLM;

/// <summary>An agent a user made in the control panel: a name, a server/slot and a system prompt, nothing else.
/// Its prompt may hold {tokens} (see <see cref="PipelineGraph"/>); a pipeline run resolves them per call and hands the
/// finished text over through <see cref="Resolve"/>, so concurrent runs never see each other's values.</summary>
internal sealed class CustomAgent : Agent
{
    private static readonly AsyncLocal<string?> resolved = new();

    /// <summary>Makes <paramref name="systemPrompt"/> this agent's system prompt for the calling async flow until disposed.</summary>
    internal static IDisposable Resolve(string systemPrompt)
    {
        string? previous = resolved.Value;
        resolved.Value = systemPrompt;
        return new Restore(previous);
    }

    internal override string BuildSystemPrompt(Thread thread) => resolved.Value ?? AgentPrompt;

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => resolved.Value = previous;
    }
}
