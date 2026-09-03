namespace ARI.LLM;

/// <summary>
/// Priority used when enqueuing a prompt. Higher value = higher priority; ties break FIFO.
/// Voice inference always jumps ahead of everything else so a live conversation is never stalled.
/// Callers that never state a priority land at the implicit default of 0 (between Normal and Background).
/// </summary>
public enum InferencePriority { Subagent = -2, Background = -1, Dream = -1, Normal = 1, Voice = 2 }

/// <summary>
/// One physical llama-server's prompt queue. Ensures only one prompt runs on that server at a time
/// (matching its single KV-cache slot) while letting higher-priority prompts jump ahead of queued
/// lower-priority ones. Every agent bound to the same server shares the same LLMQueue instance; agents
/// on different servers get separate queues, so two physically independent servers can genuinely run at
/// the same time instead of taking turns for no reason. Thread-safe; callers await AcquireAsync, do
/// their work, then dispose the returned handle.
///
/// IMPORTANT: acquire once per single prompt/turn, as close as possible to the actual HTTP call — never
/// hold a slot across a call that itself prompts the same server again (directly or through another
/// agent). This queue is a single, non-reentrant slot: a caller that holds it while waiting on a nested
/// prompt to the same queue deadlocks forever, since the nested prompt can never get the slot back.
/// </summary>
internal sealed class LLMQueue
{
    private readonly object _lock = new();
    private bool _running;

    // Each waiter is (tcs, cancellationRegistration). PriorityQueue dequeues the SMALLEST key first, but
    // InferencePriority is "bigger number = more urgent" — so waiters are enqueued under -priority,
    // making the queue's natural smallest-first order serve the highest-priority caller first.
    private readonly PriorityQueue<(TaskCompletionSource<bool> Tcs, CancellationTokenRegistration Reg), int> _waiting = new();

    /// <summary>
    /// Wait for this server's turn. Awaitable; resolves when this caller is at the front of the queue
    /// and the previous prompt on this server has finished. Dispose the returned handle to release it.
    /// </summary>
    internal async Task<IDisposable> AcquireAsync(InferencePriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        TaskCompletionSource<bool>? tcs = null;

        lock (_lock)
        {
            if (!_running)
            {
                _running = true;
                return new SlotHandle(this);
            }

            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration reg = ct.Register(() =>
            {
                lock (_lock) { tcs.TrySetCanceled(ct); }
            });
            _waiting.Enqueue((tcs, reg), -(int)priority);
        }

        try
        {
            await tcs.Task;
        }
        catch
        {
            // Cancelled while waiting — we never held the slot, nothing to release.
            throw;
        }

        return new SlotHandle(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            while (_waiting.Count > 0)
            {
                (TaskCompletionSource<bool> tcs, CancellationTokenRegistration reg) = _waiting.Dequeue();
                reg.Dispose();
                if (tcs.TrySetResult(true))
                    return; // handed off; this server's turn remains "running"
                // tcs was already cancelled — skip it and try next waiter
            }

            _running = false;
        }
    }

    private sealed class SlotHandle : IDisposable
    {
        private readonly LLMQueue _queue;
        private bool _disposed;

        internal SlotHandle(LLMQueue queue) => _queue = queue;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Release();
        }
    }
}
