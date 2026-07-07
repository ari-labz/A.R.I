using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// Static gate for "is Ari busy right now". Background work (the Scheduler, brain scans) runs only
/// while this returns true, and long-running tasks poll it to yield the moment a live thread starts —
/// so nothing Ari does in the background ever competes with a response.
/// </summary>
public static class Activity
{
    /// <summary>True when no thread is currently being processed. Defaults to true (idle) before the
    /// LLM module has registered, so startup work isn't blocked.</summary>
    public static bool IsIdle() => Modules.Llm?.IsIdle ?? true;
}
