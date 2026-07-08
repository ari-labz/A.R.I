using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Standalone copy of DialoguePipeline (issue #84): identical behaviour to start, a baseline the Speech
// pipeline diverges from through later Speech-Pipeline issues. Deliberately does NOT inherit Dialogue —
// it reuses the Dialogue agent instance for now so a Speech thread behaves exactly like a Dialogue one.
internal sealed class SpeechPipeline : Pipeline
{
    private readonly Dialogue  dialogue;
    private readonly Memory?   memory;
    private readonly Context?  context;
    private readonly Engram?   engram;

    protected override Agent  PrimaryAgent => dialogue;
    protected override string PipelineName => "Speech";

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal SpeechPipeline(
        Dialogue  dialogue,
        Memory?   memory,
        Context?  context,
        Engram?   engram,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.dialogue = dialogue;
        this.memory   = memory;
        this.context  = context;
        this.engram   = engram;

        dialogue.ThreadBufferFull    += key => ThreadBufferFull?.Invoke(key);
        dialogue.ThreadBecameInactive += key => ThreadBecameInactive?.Invoke(key);
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Speech", threadKey, 0, dialogue.BudgetResponse, dialogue.BudgetContext, dialogue.BudgetImage);

    protected override async Task<string> RunAsync(
        Thread               thread,
        string               threadKey,
        string               effectivePrompt,
        string               username,
        string?              platformContext,
        Func<string, Task>?  onDelta,
        CancellationTokenSource cts,
        string?              localPath)
    {
        if (engram?.IsSweeping(threadKey) == true)
        {
            Shared.Logger.LogInformation("[Speech] ({Thread}) waiting for Engram sweep to finish before processing.", threadKey);
            await engram.WaitForSweep(threadKey, cts.Token);
        }

        Shared.Logger.LogInformation("[Speech] ({Thread}) prompt\n\"{Prompt}\"", threadKey, effectivePrompt);

        string? contextSummary = context?.GetContext(threadKey);

        string? recallBlock = null;
        if (memory is not null)
        {
            try
            {
                List<ThreadMessage> chatHistory = thread.GetChatHistory();
                recallBlock = await memory.GetNotes(chatHistory, effectivePrompt, contextSummary, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Superseded by a newer message (common on Discord) — abort quietly, don't surface as an error.
                throw;
            }
            catch (Exception ex)
            {
                Shared.Logger.LogError(ex, "[Memory] ({Thread}) Failed to retrieve memories from {Platform}: {Error}", threadKey, platformContext ?? "unknown", ex.Message);
                string errorMessage = "> error retrieving memories";
                if (onDelta is not null) await onDelta(errorMessage);
                return errorMessage;
            }
        }

        return await dialogue.SendPrompt(
            thread, effectivePrompt, username,
            recallNotes:     recallBlock,
            contextSummary:  contextSummary,
            ct:              cts.Token,
            userMessagePreadded: true,
            onDelta:         onDelta);
    }
}
