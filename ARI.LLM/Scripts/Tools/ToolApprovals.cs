using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// Asks the user to approve a risky action a server-side tool is about to take (a destructive git or gh
/// command). The tool awaits <see cref="RequestAsync"/>; the pending request rides the thread's watch stream
/// to the app, which shows an Allow / Deny prompt and answers via the approvals API. No answer within
/// <see cref="Timeout"/>, or a thread with nobody to ask (Discord, background agents), is a denial.
/// </summary>
internal static class ToolApprovals
{
    internal sealed record Pending(string Id, string Title, string Command);

    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Pushes a thread's watch update — set by LLMModule, the same hook the agents use.</summary>
    internal static Action<string>? Notify;

    // One open request per thread: a turn runs its tool calls one at a time.
    private static readonly ConcurrentDictionary<string, (Pending Request, TaskCompletionSource<bool> Answer)> open = new();

    /// <summary>True if the user allowed it; false if they denied it, didn't answer in time, or can't be asked.</summary>
    internal static async Task<bool> RequestAsync(Thread thread, string title, string command)
    {
        if (!thread.IsAdminChat) return false;

        Pending request = new(Guid.NewGuid().ToString("N"), title, command);
        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        open[thread.Key] = (request, answer);
        Notify?.Invoke(thread.Key);
        try { return await answer.Task.WaitAsync(Timeout, thread.Ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return false; }
        finally
        {
            open.TryRemove(thread.Key, out _);
            Notify?.Invoke(thread.Key);
        }
    }

    /// <summary>The request waiting on this thread, if any — sent to the app with each watch update.</summary>
    internal static Pending? Get(string threadKey) => open.TryGetValue(threadKey, out var o) ? o.Request : null;

    /// <summary>Answers a waiting request. False if it's no longer open (already answered, timed out, or a stale id).</summary>
    internal static bool Resolve(string threadKey, string id, bool allow)
        => open.TryGetValue(threadKey, out var o) && o.Request.Id == id && o.Answer.TrySetResult(allow);
}
