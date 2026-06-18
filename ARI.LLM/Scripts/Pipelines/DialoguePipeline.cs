using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class DialoguePipeline : Pipeline
{
    private readonly Dialogue  dialogue;
    private readonly Memory?   memory;
    private readonly Context?  context;
    private readonly Engram?   engram;

    protected override Agent  PrimaryAgent => dialogue;
    protected override string PipelineName => "Dialogue";

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal DialoguePipeline(
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
        new("Dialogue", threadKey, 0, dialogue.MaxTokens, dialogue.MaxContextTokens, dialogue.MaxImageTokens);

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
            Shared.Logger.LogInformation("[Dialogue] ({Thread}) waiting for Engram sweep to finish before processing.", threadKey);
            await engram.WaitForSweep(threadKey, cts.Token);
        }

        Shared.Logger.LogInformation("[Dialogue] ({Thread}) prompt\n\"{Prompt}\"", threadKey, effectivePrompt);

        string? contextSummary = context?.GetContext(threadKey);

        string? recallBlock = null;
        if (memory is not null)
        {
            try
            {
                List<ThreadMessage> chatHistory = thread.GetChatHistory();
                recallBlock = await memory.GetNotes(chatHistory, effectivePrompt, contextSummary, cts.Token);
            }
            catch (Exception ex)
            {
                Shared.Logger.LogError("[Memory] Failed to retrieve memories: {Error}", ex.Message);
                string errorMessage = "> error retrieving memories";
                if (onDelta is not null) await onDelta(errorMessage);
                return errorMessage;
            }
        }

        return await dialogue.SendPrompt(
            threadKey, effectivePrompt, username,
            platformContext: platformContext,
            recallNotes:     recallBlock,
            contextSummary:  contextSummary,
            ct:              cts.Token,
            userMessagePreadded: true,
            onDelta:         onDelta);
    }
}
