namespace ARI.LLM;

/// <summary>
/// Drives the dream pipeline. Runs freely with read-only tools and Wake. No Engram, no memory
/// prefill, no user — ARI self-drives via OnStepComplete until she calls Wake or the dream is
/// interrupted by a real user request.
/// </summary>
internal sealed class Dreamer : Agent
{
    internal ToolResult? WakeRequest { get; private set; }

    internal override bool UseSystemContinuation => true;

    // Re-enter the loop after each step so ARI keeps thinking without an external nudge.
    // Returns null once Wake has been called — the pipeline checks WakeRequest and breaks.
    // Filesystem tools unlock inside bind_project itself (same step), so no re-registration needed here.
    internal override string? OnStepComplete(Thread thread, string stepText, bool hadTools)
    {
        if (WakeRequest is not null) return null;
        return "(continue)";
    }

    internal void RecordWake(ToolResult result) => WakeRequest = result;
    internal void ResetWake()                   => WakeRequest = null;
}
