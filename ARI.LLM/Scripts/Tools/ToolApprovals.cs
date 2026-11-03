using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// Asks the user to approve a risky action a server-side tool is about to take (a destructive git or gh
/// command). The tool awaits <see cref="RequestAsync"/>; the pending request rides the thread's watch stream
/// to the app, which shows an Allow / Deny prompt and answers via the approvals API. No answer within
/// <see cref="Timeout"/>, or a thread with nobody to ask (Discord, background agents), is a denial.
/// </summary>
/// <summary>The owner's answer to an approval prompt. Always stores the command's key on the allowlist (only offered for an
/// allowable command).</summary>
internal enum ApprovalDecision { Deny, Allow, Always }

internal static class ToolApprovals
{
    /// <summary>A waiting prompt. Segment is set for a shell command, so the app can offer "Allow … every time".</summary>
    internal sealed record Pending(string Id, string Title, string Command, CommandSegment? Segment = null);

    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Pushes a thread's watch update — set by LLMModule, the same hook the agents use.</summary>
    internal static Action<string>? Notify;

    // One open request per thread: a turn runs its tool calls one at a time.
    private static readonly ConcurrentDictionary<string, (Pending Request, TaskCompletionSource<ApprovalDecision> Answer)> open = new();

    /// <summary>True if the user allowed it; false if they denied it, didn't answer in time, or can't be asked.</summary>
    internal static async Task<bool> RequestAsync(Thread thread, string title, string command) =>
        await DecideAsync(thread, title, command) != ApprovalDecision.Deny;

    /// <summary>Asks about one command and returns the answer. Deny when they said no, didn't answer in time, or can't be asked.</summary>
    internal static async Task<ApprovalDecision> DecideAsync(Thread thread, string title, string command, CommandSegment? segment = null)
    {
        if (!thread.IsAdminChat) return ApprovalDecision.Deny;

        Pending request = new(Guid.NewGuid().ToString("N"), title, command, segment);
        TaskCompletionSource<ApprovalDecision> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        open[thread.Key] = (request, answer);
        Notify?.Invoke(thread.Key);
        try { return await answer.Task.WaitAsync(Timeout, thread.Ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return ApprovalDecision.Deny; }
        finally
        {
            open.TryRemove(thread.Key, out _);
            Notify?.Invoke(thread.Key);
        }
    }

    /// <summary>The request waiting on this thread, if any — sent to the app with each watch update.</summary>
    internal static Pending? Get(string threadKey) => open.TryGetValue(threadKey, out var o) ? o.Request : null;

    /// <summary>Answers a waiting request. False if it's no longer open (already answered, timed out, or a stale id).</summary>
    internal static bool Resolve(string threadKey, string id, ApprovalDecision decision)
        => open.TryGetValue(threadKey, out var o) && o.Request.Id == id && o.Answer.TrySetResult(decision);
}
