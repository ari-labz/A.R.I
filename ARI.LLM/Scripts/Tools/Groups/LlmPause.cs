using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Runs a memory-hungry job (a mesh, an image) with the language-model servers stopped, then starts them again. The
/// stopped servers lose their cached chats, so the next reply re-reads the conversation from the start; that wait is
/// the cost of freeing the memory. Jobs queue one at a time so two never restart the servers under each other.
/// </summary>
internal static class LlmPause
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <param name="stopLlms">The module's switch: allowed to stop the language models.</param>
    /// <param name="what">Shown to the user while paused, e.g. "making a 3D model".</param>
    /// <param name="needGb">Memory the job takes. When that much is free on top of the reserve, the models are left running.</param>
    internal static async Task<T> Around<T>(bool stopLlms, string what, double needGb, Func<Task<T>> job)
    {
        if (!stopLlms || Modules.Llm is null) return await job();
        (double _, double freeGb) = SystemMemory.Read();
        if (freeGb >= needGb + SystemMemory.ReservedGb)
        {
            Shared.Logger.LogInformation("[LlmPause] {Free} GB free is enough for {What}; leaving the language models running.", freeGb, what);
            return await job();
        }

        await Gate.WaitAsync();
        try
        {
            Shared.Logger.LogInformation("[LlmPause] Stopping the language models to free memory for a generation job.");
            Modules.LlmPausedFor = what;
            await Modules.Llm.StopAllServersAsync();
            try { return await job(); }
            finally
            {
                Shared.Logger.LogInformation("[LlmPause] Job finished. Starting the language models again.");
                try { await Modules.Llm.RestartAllServersAsync(); }
                finally { Modules.LlmPausedFor = null; }
            }
        }
        finally { Gate.Release(); }
    }
}
