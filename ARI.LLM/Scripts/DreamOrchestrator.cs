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
    // Set when a dream ends with nothing left to look at: the next waits until someone has talked to her again. After a wake, another
    // dream can follow straight on, and it is told which subjects this quiet stretch has already covered.
    private bool                      dreamedSinceActivity;
    private DateTime                  restUntil;
    private static readonly TimeSpan  REST_AFTER_QUIET_DREAM = TimeSpan.FromHours(3);
    // She asked to sleep: no dream until then, and the next one starts with her note.
    private DateTime?                 sleepUntil;
    private string                    sleepNote = "";
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
        dreamer.ToolCalled      = name => log.RecordToolCall(name);
    }

    internal void NotifyUserActivity()
    {
        lastUserActivity     = DateTime.Now;
        dreamedSinceActivity = false;
        sleepUntil           = null;
        sleepNote            = "";
        dreamer.ForgetSubjects();
        InterruptDream();
        // They're back: a message she was holding for the morning is said now.
        try { pipeline.SendHeldWake(now: true); } catch (Exception ex) { Shared.Logger.LogWarning("[Dream] Couldn't send the held wake message: {Err}", ex.Message); }
    }

    internal void Start() => Task.Run(WatchLoop);

    private async Task WatchLoop()
    {
        while (!shutdownCts.IsCancellationRequested)
        {
            await Task.Delay(POLL_INTERVAL_MS, shutdownCts.Token).ConfigureAwait(false);

            // A message she decided on and is holding until a set time: nothing else happens until it's sent. Waiting to say it isn't
            // idle time, so no new dream starts; once it's sent, the quiet stretch counts from then.
            if (HeldWake.Current is not null)
            {
                try { if (pipeline.SendHeldWake(now: false)) lastUserActivity = DateTime.Now; }
                catch (Exception ex) { Shared.Logger.LogWarning("[Dream] Couldn't send the held wake message: {Err}", ex.Message); }
                continue;
            }

            if (!isDreamingEnabled()) continue;

            // If the last turn ended because Wake was called, close the session and reset
            // so the next idle period starts a fresh dream rather than continuing the same thread.
            if (dreamTask is { IsCompleted: true } && dreamer.WakeRequest is not null)
            {
                CloseDream();
                dreamer.ResetWake();
            }

            if (dreamTask is { IsCompleted: false }) continue;
            if (dreamedSinceActivity && DateTime.Now < restUntil) continue;   // resting after a quiet dream, but only for a while
            if (dreamedSinceActivity) { dreamedSinceActivity = false; Shared.Logger.LogInformation("[Dream] Rest over — dreaming again."); }
            if (sleepUntil is { } until)
            {
                if (DateTime.Now < until) continue;
                sleepUntil = null;   // time to carry on: the note goes into this dream's opening, then is cleared
            }

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

        dreamer.BeginDream();
        Sleep.Requested = null;
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
                platformContext: Anchor(),
                onDelta:         null,
                cts:             cts);
            consecutiveFailures = 0;
            nextRetryAt         = DateTime.MinValue;
            // She woke someone (another dream may follow), or finished her last subject (rest until someone talks to her).
            bool woke = dreamer.WakeRequest is not null;
            if (dreamer.WakeRequest is { } wake) log.RecordWake(wake.SendAt is { } at ? $"(held until {at:HH:mm}) {wake.Text}" : wake.Text);
            if (!woke && Sleep.Requested is { } sleep)
            {
                // She chose to sleep: not done for the night, just waiting. Carry on at that time with her note.
                sleepUntil = sleep.Until;
                sleepNote  = sleep.Note;
                Shared.Logger.LogInformation("[Dream] Sleeping until {At:HH:mm}.", sleep.Until);
                FinishDream(rest: false);
                return;
            }
            FinishDream(rest: !woke);
        }
        catch (OperationCanceledException)
        {
            Shared.Logger.LogInformation("[Dream] Turn interrupted — will resume when idle again.");
        }
        catch (Exception ex) when (ex.Message.Contains("exceed_context_size", StringComparison.Ordinal))
        {
            // Out of room: starting over would only repeat the same reading from scratch, so the dream ends here.
            Shared.Logger.LogWarning("[Dream] Ran out of context; ending the dream until the next quiet stretch.");
            FinishDream(rest: true);
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

    /// <summary>The dream's opening context: the time, what recent dreams woke about, a starting point, and the subjects already covered.</summary>
    private string Anchor()
    {
        string anchor = DreamAnchor.PullWithGrounding();
        if (sleepNote.Length > 0)
        {
            anchor += "\n\nYou slept until now. Before you slept you wrote yourself this note: " + sleepNote;
            sleepNote = "";
        }
        return dreamer.CoveredSubjects.Count == 0 ? anchor
            : anchor + "\n\nEarlier dreams since your master was last here already covered: " + string.Join("; ", dreamer.CoveredSubjects) + ". Pick something else.";
    }

    /// <summary>Closes a finished dream. With rest, no new one starts until someone talks to her again.</summary>
    private void FinishDream(bool rest)
    {
        if (rest)
        {
            // A dream that found nothing worth a message rests for a few hours, not until someone speaks: one quiet dream must not end
            // the whole night, or she never gets the chance to wake anyone.
            dreamedSinceActivity = true;
            restUntil            = DateTime.Now.Add(REST_AFTER_QUIET_DREAM);
            Shared.Logger.LogInformation("[Dream] Ended without waking anyone; resting until {At:HH:mm}.", restUntil);
        }
        dreamTask            = null;
        dreamCts             = null;   // the pipeline has already disposed it: cancelling it now would throw
        CloseDream();
        dreamer.ResetWake();
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
