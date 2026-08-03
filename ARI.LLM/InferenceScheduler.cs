namespace ARI.LLM;

/// <summary>
/// Priority used when enqueuing an inference request. Lower value = higher priority.
/// Voice inference jumps ahead of text so a live conversation is never stalled behind a chat message.
/// </summary>
public enum InferencePriority { Voice = 0, Normal = 1 }

/// <summary>
/// Global single-slot scheduler for llama.cpp inference requests. Ensures only one request runs at a
/// time (matching the server's single KV-cache slot) while letting voice requests jump ahead of queued
/// text requests. Thread-safe; callers await AcquireAsync, do their work, then dispose the returned handle.
/// </summary>
internal sealed class InferenceScheduler
{
    private readonly object _lock = new();
    private bool _running;

    // Each waiter is (tcs, cancellationRegistration). Priority: 0=Voice, 1=Normal.
    private readonly PriorityQueue<(TaskCompletionSource<bool> Tcs, CancellationTokenRegistration Reg), int> _waiting = new();

    /// <summary>
    /// Acquire the inference slot. Awaitable; resolves when this caller is at the front of the queue
    /// and the previous inference has finished. Dispose the returned handle to release the slot.
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
            _waiting.Enqueue((tcs, reg), (int)priority);
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
                    return; // handed off; slot remains "running"
                // tcs was already cancelled — skip it and try next waiter
            }

            _running = false;
        }
    }

    private sealed class SlotHandle : IDisposable
    {
        private readonly InferenceScheduler _scheduler;
        private bool _disposed;

        internal SlotHandle(InferenceScheduler scheduler) => _scheduler = scheduler;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _scheduler.Release();
        }
    }
}
