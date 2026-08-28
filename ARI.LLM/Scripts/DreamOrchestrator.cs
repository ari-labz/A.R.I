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

    private readonly DreamPipeline                                         pipeline;
    private readonly Dreamer                                               dreamer;
    private readonly InferenceScheduler                                    scheduler;
    private readonly Func<bool>                                            isDreamingEnabled;
    private readonly Func<Thread>                                          createDreamThread;
    private readonly Action<Thread>                                        destroyDreamThread;
    private readonly DreamLog                                              log;

    private Thread?                   dreamThread;
    private CancellationTokenSource?  dreamCts;
    private Task?                     dreamTask;
    private DateTime                  lastUserActivity = DateTime.MinValue;
    private readonly CancellationTokenSource shutdownCts = new();

    internal DreamOrchestrator(
        DreamPipeline       pipeline,
        Dreamer             dreamer,
        InferenceScheduler  scheduler,
        Func<bool>          isDreamingEnabled,
        Func<Thread>        createDreamThread,
        Action<Thread>      destroyDreamThread)
    {
        this.pipeline           = pipeline;
        this.dreamer            = dreamer;
        this.scheduler          = scheduler;
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
        IDisposable slot;
        try { slot = await scheduler.AcquireAsync(InferencePriority.Dream, cts.Token); }
        catch (OperationCanceledException) { return; }

        try
        {
            using (slot)
                await pipeline.ExecuteAsync(
                    dreamThread!,
                    dreamThread!.Key,
                    prompt:          "",
                    username:        "dream",
                    platformContext: DreamAnchor.Pull(),
                    onDelta:         null,
                    cts:             cts);
        }
        catch (OperationCanceledException)
        {
            Shared.Logger.LogInformation("[Dream] Turn interrupted — will resume when idle again.");
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Dream] Turn failed.");
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
