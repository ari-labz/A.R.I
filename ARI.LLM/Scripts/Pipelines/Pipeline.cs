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

        string effectivePrompt = thread.History.Count > 0 && thread.History[^1] is UserMessage prev
            ? prev.Content + "\n" + prompt
            : prompt;

        thread.AddItem(new UserMessage
        {
            Username    = username,
            Content     = prompt,
            Timestamp   = DateTime.Now,
            Attachments = messageAttachments is { Count: > 0 } ? messageAttachments : null,
        });

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
