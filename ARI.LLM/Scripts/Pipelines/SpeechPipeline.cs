using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class SpeechPipeline : Pipeline
{
    private readonly TalkingAgent talkingAgent;
    private readonly Memory?      memory;
    private readonly Context?     context;
    private readonly Engram?      engram;

    // Pending speech steering contexts, keyed by thread key.
    // Set by LLMModule before ExecuteAsync, consumed and cleared in RunAsync.
    private readonly ConcurrentDictionary<string, SpeechSteeringContext?> _pendingSteering = new();

    protected override Agent  PrimaryAgent => talkingAgent;
    protected override string PipelineName => "Speech";

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal SpeechPipeline(
        TalkingAgent talkingAgent,
        Memory?      memory,
        Context?     context,
        Engram?      engram,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.talkingAgent = talkingAgent;
        this.memory       = memory;
        this.context      = context;
        this.engram       = engram;

        talkingAgent.ThreadBufferFull     += key => ThreadBufferFull?.Invoke(key);
        talkingAgent.ThreadBecameInactive += key => ThreadBecameInactive?.Invoke(key);
    }

    internal void SetSteering(string threadKey, SpeechSteeringContext? ctx)
        => _pendingSteering[threadKey] = ctx;

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Speech", threadKey, 0, talkingAgent.BudgetResponse, talkingAgent.BudgetContext, talkingAgent.BudgetImage);

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

        _pendingSteering.TryRemove(threadKey, out SpeechSteeringContext? steering);

        talkingAgent.Steering = steering;
        try
        {
            return await talkingAgent.Prompt(thread, effectivePrompt, new PromptOptions
            {
                Username            = username,
                RecallNotes         = recallBlock,
                ContextSummary      = contextSummary,
                Ct                  = cts.Token,
                UserMessagePreadded = true,
                OnDelta             = onDelta,
            });
        }
        finally
        {
            talkingAgent.Steering = null;
        }
    }
}
