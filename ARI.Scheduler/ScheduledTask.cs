using Cronos;

namespace ARI.Scheduler;

/// <summary>
/// One cron-scheduled background job. Jobs run whenever their slot comes round — busy or not — and
/// are handed a CancellationToken that fires on shutdown or when stopped from the control panel.
/// A job that is stopped consumes its slot: it waits for the next scheduled time rather than
/// resuming, so nothing is ever run late.
/// </summary>
internal sealed class ScheduledTask
{
    internal string Name { get; }
    internal CronExpression Cron { get; private set; }
    internal string CronText { get; private set; }
    internal Func<CancellationToken, Task> Handler { get; }

    // Persisted: when the task last ran. Cron's next occurrence is computed from this (or from
    // server start, whichever is later — see Floor).
    internal DateTime LastRunUtc { get; set; }

    // When true, a due slot is held (not run) while Ari is actively in conversation, and re-checked
    // after DeferWindow. The memory walks (Refactor/Curiosity) opt in; jobs like ProactiveMessage don't.
    internal bool RespectActivity { get; }

    // Activity-deferral state for the CURRENT due slot. DeferredUntil holds the next re-check time;
    // DeferCount counts how many times this one slot has been pushed back (capped, then the slot is
    // dropped and the task waits for its next cron occurrence). Both reset when the slot runs or is dropped.
    internal DateTime? DeferredUntil { get; set; }
    internal int       DeferCount    { get; set; }

    internal ScheduledTask(string name, string cronExpression, Func<CancellationToken, Task> handler, DateTime lastRunUtc, bool respectActivity = false)
    {
        Name = name;
        CronText = cronExpression;
        Cron = CronExpression.Parse(cronExpression);
        Handler = handler;
        LastRunUtc = lastRunUtc;
        RespectActivity = respectActivity;
    }

    /// <summary>Swaps in a new cron expression live. Throws CronFormatException if invalid (caller validates).</summary>
    internal void UpdateCron(string cronExpression)
    {
        Cron = CronExpression.Parse(cronExpression);
        CronText = cronExpression;
    }

    // Slots are only owed from the point the server was up: the later of the last run and server
    // start. A slot that passed while Ari was off is never caught up on — the next occurrence is
    // measured from startup instead — which is also why nothing fires just because she booted.
    private DateTime Floor(DateTime startedUtc) => LastRunUtc > startedUtc ? LastRunUtc : startedUtc;

    /// <summary>Next scheduled fire time (UTC), or null if none.</summary>
    internal DateTime? NextRunUtc(DateTime startedUtc) => Cron.GetNextOccurrence(Floor(startedUtc), TimeZoneInfo.Utc);

    /// <summary>Due once a scheduled slot has passed while the server was up.</summary>
    internal bool IsDue(DateTime nowUtc, DateTime startedUtc)
    {
        DateTime? next = Cron.GetNextOccurrence(Floor(startedUtc), TimeZoneInfo.Utc);
        return next.HasValue && next.Value <= nowUtc;
    }
}
