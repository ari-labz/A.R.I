using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Watches thread activity and runs the dream pipeline whenever ARI is idle.
/// The dream thread is lowest-priority inference — it yields the moment any user thread activates.
/// Each boot starts a fresh dream; no dream state persists across restarts.
/// </summary>
internal sealed class DreamOrchestrator : IDisposable
{
    private const int IDLE_GRACE_SECONDS  = 120;  // wait this long after last activity before dreaming
    private const int POLL_INTERVAL_MS    = 10_000;
    private const int MAX_BACKOFF_SECONDS = 300;   // cap the retry delay after repeated failures (e.g. LLM servers down)

    private readonly DreamPipeline                                         pipeline;
    private readonly Dreamer                                               dreamer;
    private readonly LLMQueue                                              queue;
    private readonly Func<bool>                                            isDreamingEnabled;
    private readonly Func<Thread>                                          createDreamThread;
    private readonly Action<Thread>                                        destroyDreamThread;
    private readonly DreamLog                                              log;

    private Thread?                   dreamThread;
    private CancellationTokenSource?  dreamCts;
    private Task?                     dreamTask;
    private DateTime                  lastUserActivity = DateTime.MinValue;
    private int                       consecutiveFailures = 0;
    private DateTime                  nextRetryAt = DateTime.MinValue;
    private readonly CancellationTokenSource shutdownCts = new();

    internal DreamOrchestrator(
        DreamPipeline       pipeline,
        Dreamer             dreamer,
        LLMQueue            queue,
        Func<bool>          isDreamingEnabled,
        Func<Thread>        createDreamThread,
        Action<Thread>      destroyDreamThread)
    {
        this.pipeline           = pipeline;
        this.dreamer            = dreamer;
        this.queue              = queue;
        this.isDreamingEnabled  = isDreamingEnabled;
        this.createDreamThread  = createDreamThread;
        this.destroyDreamThread = destroyDreamThread;
        this.log                = new DreamLog();
    }

    internal void NotifyUserActivity()
    {
        lastUserActivity = DateTime.Now;
        InterruptDream();
    }

    internal void Start() => Task.Run(WatchLoop);

    private async Task WatchLoop()
    {
        while (!shutdownCts.IsCancellationRequested)
        {
            await Task.Delay(POLL_INTERVAL_MS, shutdownCts.Token).ConfigureAwait(false);

            if (!isDreamingEnabled()) continue;

            // If the last turn ended because Wake was called, close the session and reset
            // so the next idle period starts a fresh dream rather than continuing the same thread.
            if (dreamTask is { IsCompleted: true } && dreamer.WakeRequest is not null)
            {
                CloseDream();
                dreamer.ResetWake();
            }

            if (dreamTask is { IsCompleted: false }) continue;

            // Back off after repeated failures (e.g. the LLM servers are stopped) instead of
            // retrying every poll tick forever, spamming the log with the same connection error.
            if (DateTime.Now < nextRetryAt) continue;

            TimeSpan idleFor = DateTime.Now - lastUserActivity;
            if (idleFor.TotalSeconds < IDLE_GRACE_SECONDS) continue;

            await RunDreamTurnAsync().ConfigureAwait(false);
        }
    }

    private async Task RunDreamTurnAsync()
    {
        if (dreamThread is null)
        {
            dreamThread = createDreamThread();
            log.Begin(dreamThread.Key);
            Shared.Logger.LogInformation("[Dream] Session started on thread {Key}.", dreamThread.Key);
        }

        dreamCts  = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        dreamTask = ExecuteDreamTurn(dreamCts);
    }

    private async Task ExecuteDreamTurn(CancellationTokenSource cts)
    {
        // No outer acquire here — pipeline.ExecuteAsync -> dreamer.Prompt already acquires this queue
        // itself, per step. Holding it here too would deadlock: this call could never release it until
        // the nested prompt finishes, and the nested prompt can never start without it back.
        try
        {
            await pipeline.ExecuteAsync(
                dreamThread!,
                dreamThread!.Key,
                prompt:          "",
                username:        "dream",
                platformContext: DreamAnchor.PullWithGrounding(),
                onDelta:         null,
                cts:             cts);
            consecutiveFailures = 0;
            nextRetryAt         = DateTime.MinValue;
        }
        catch (OperationCanceledException)
        {
            Shared.Logger.LogInformation("[Dream] Turn interrupted — will resume when idle again.");
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            int backoffSeconds = Math.Min(POLL_INTERVAL_MS / 1000 * consecutiveFailures, MAX_BACKOFF_SECONDS);
            nextRetryAt = DateTime.Now.AddSeconds(backoffSeconds);
            // Full stack trace on the first failure of a streak only — after that it's almost
            // certainly the same cause (e.g. the LLM servers are stopped), so log a terse line.
            if (consecutiveFailures == 1)
                Shared.Logger.LogWarning(ex, "[Dream] Turn failed. Backing off for {Seconds}s.", backoffSeconds);
            else
                Shared.Logger.LogWarning("[Dream] Turn failed again ({Count} in a row): {Error}. Backing off for {Seconds}s.",
                    consecutiveFailures, ex.Message, backoffSeconds);
        }
        finally
        {
            // Pipeline.ExecuteAsync disposes the CTS in its own finally block. Null it here so
            // InterruptDream/NotifyUserActivity never calls Cancel() on an already-disposed CTS.
            if (ReferenceEquals(dreamCts, cts)) dreamCts = null;
        }
    }

    private void InterruptDream()
    {
        dreamCts?.Cancel();
    }

    private void CloseDream()
    {
        InterruptDream();
        if (dreamThread is not null)
        {
            log.End(dreamThread.Key);
            destroyDreamThread(dreamThread);
            Shared.Logger.LogInformation("[Dream] Session closed.");
        }
        dreamThread = null;
        dreamCts    = null;
        dreamTask   = null;
    }

    public void Dispose()
    {
        shutdownCts.Cancel();
        try { CloseDream(); } catch { /* cancellation during shutdown is expected */ }
        shutdownCts.Dispose();
    }
}
