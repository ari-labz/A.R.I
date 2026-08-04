using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class DialoguePipeline : Pipeline
{
    private readonly TextingAgent textingAgent;
    private readonly Memory?      memory;
    private readonly Context?     context;
    private readonly Engram?      engram;

    protected override Agent  PrimaryAgent => textingAgent;
    protected override string PipelineName => "Dialogue";

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;

    internal DialoguePipeline(
        TextingAgent textingAgent,
        Memory?      memory,
        Context?     context,
        Engram?      engram,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.textingAgent = textingAgent;
        this.memory       = memory;
        this.context      = context;
        this.engram       = engram;

        textingAgent.ThreadBufferFull     += key => ThreadBufferFull?.Invoke(key);
        textingAgent.ThreadBecameInactive += key => ThreadBecameInactive?.Invoke(key);
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Dialogue", threadKey, 0, textingAgent.BudgetResponse, textingAgent.BudgetContext, textingAgent.BudgetImage);

    /// <summary>Runs memory recall + Dialogue for a proactive draft thread. The question drives memory
    /// recall; the instruction is injected as a trailing system nudge so Ari writes the opener rather
    /// than acknowledging a command.</summary>
    internal async Task<string> RunProactiveAsync(
        Thread                  thread,
        string                  threadKey,
        string                  question,
        string                  instruction,
        CancellationTokenSource cts)
    {
        string? contextSummary = context?.GetContext(threadKey);
        string? recallBlock    = null;
        double? recallSeconds  = null;
        if (memory is not null)
        {
            var recallSw = System.Diagnostics.Stopwatch.StartNew();
            try   { recallBlock = await memory.GetNotes(new List<ThreadMessage>(), question, contextSummary, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
            catch (Exception ex) { Shared.Logger.LogWarning(ex, "[Proactive] Memory recall failed — drafting without memories."); }
            recallSeconds = recallSw.Elapsed.TotalSeconds;
        }

        return await textingAgent.Prompt(thread, question, new PromptOptions
        {
            RecallNotes    = recallBlock,
            RecallSeconds  = recallSeconds,
            ModeNudge      = instruction,
            Ct             = cts.Token,
        });
    }

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
        double? recallSeconds = null;
        if (memory is not null)
        {
            var recallSw = System.Diagnostics.Stopwatch.StartNew();
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
                Shared.Logger.LogWarning(ex, "[Memory] ({Thread}) Memory recall failed — continuing without memories. {Error}", threadKey, ex.Message);
                recallBlock = "Memory recall is unavailable right now — the memory server appears to be offline. Tell the user at the start of your response that you cannot access your memories at the moment and that your answer may be inaccurate or incomplete as a result. Then respond as best you can.";
            }
            recallSeconds = recallSw.Elapsed.TotalSeconds;
        }

        return await textingAgent.Prompt(thread, effectivePrompt, new PromptOptions
        {
            Username            = username,
            RecallNotes         = recallBlock,
            RecallSeconds       = recallSeconds,
            ContextSummary      = contextSummary,
            Ct                  = cts.Token,
            UserMessagePreadded = true,
            OnDelta             = onDelta,
        });
    }
}
