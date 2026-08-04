using System.Collections.Concurrent;

namespace ARI.LLM;

internal abstract class Pipeline
{
    protected abstract Agent   PrimaryAgent { get; }
    protected abstract string  PipelineName { get; }

    private readonly ConcurrentDictionary<string, CancellationTokenSource> processingThreads;
    private readonly ConcurrentDictionary<string, LiveCallInfo>             liveCalls;
    private readonly Action<string>                                          notifyWatchers;

    protected Pipeline(
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
    {
        this.processingThreads = processingThreads;
        this.liveCalls         = liveCalls;
        this.notifyWatchers    = notifyWatchers;
    }

    internal async Task<string> ExecuteAsync(
        Thread               thread,
        string               threadKey,
        string               prompt,
        string               username,
        string?              platformContext,
        Func<string, Task>?  onDelta,
        CancellationTokenSource cts,
        List<Attachment>?    messageAttachments = null,
        List<Attachment>?    threadAttachments  = null,
        string?              localPath          = null)
    {
        if (threadAttachments is { Count: > 0 })
            foreach (Attachment a in threadAttachments)
                thread.AddAttachment(a);

        LiveCallInfo liveCall = BuildLiveCall(threadKey);
        liveCalls[threadKey] = liveCall;
        thread.SetLiveCall(liveCall);

        string effectivePrompt = thread.History.Count > 0 && thread.History[^1] is Prompt prev
            ? prev.Text + "\n" + prompt
            : prompt;

        thread.AddItem(new Prompt
        {
            AuthorName  = username,
            Text        = prompt,
            Timestamp   = DateTime.Now,
            Attachments = messageAttachments is { Count: > 0 } ? messageAttachments : null,
        });

        // Everything this prompt sets off — Memory's recall, Context's summariser, the primary agent,
        // any sub-thread — records under one exchange id, so the fan-out reassembles from its separate
        // files later. The scope is async-local, so concurrent threads never share one.
        using IDisposable? exchange = SessionRecorder.BeginExchange(threadKey, username, prompt);

        try
        {
            return await RunAsync(thread, threadKey, effectivePrompt, username, platformContext, onDelta, cts, localPath);
        }
        catch (OperationCanceledException)
        {
            thread.preserveOnCancel = false;
            throw;
        }
        finally
        {
            liveCalls.TryRemove(threadKey, out _);
            thread.ClearMessageAttachments();
            processingThreads.TryRemove(new KeyValuePair<string, CancellationTokenSource>(threadKey, cts));
            cts.Dispose();
            notifyWatchers(threadKey);
        }
    }

    protected abstract LiveCallInfo BuildLiveCall(string threadKey);

    protected abstract Task<string> RunAsync(
        Thread               thread,
        string               threadKey,
        string               effectivePrompt,
        string               username,
        string?              platformContext,
        Func<string, Task>?  onDelta,
        CancellationTokenSource cts,
        string?              localPath);
}
