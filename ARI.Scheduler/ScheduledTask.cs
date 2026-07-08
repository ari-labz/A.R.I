using Cronos;

namespace ARI.Scheduler;

/// <summary>
/// One cron-scheduled background job. The handler runs only while Ari is idle; it is handed a
/// CancellationToken that fires the moment Ari becomes busy, so a long job (e.g. a brain scan)
/// can checkpoint and yield. A job that is cancelled mid-run is NOT marked complete, so it stays
/// due and resumes on the next idle window.
/// </summary>
internal sealed class ScheduledTask
{
    internal string Name { get; }
    internal CronExpression Cron { get; private set; }
    internal string CronText { get; private set; }
    internal Func<CancellationToken, Task> Handler { get; }

    // Persisted: when the task last completed a full run. Cron's next occurrence is computed from this.
    internal DateTime LastRunUtc { get; set; }

    internal ScheduledTask(string name, string cronExpression, Func<CancellationToken, Task> handler, DateTime lastRunUtc)
    {
        Name = name;
        CronText = cronExpression;
        Cron = CronExpression.Parse(cronExpression);
        Handler = handler;
        LastRunUtc = lastRunUtc;
    }

    /// <summary>Swaps in a new cron expression live. Throws CronFormatException if invalid (caller validates).</summary>
    internal void UpdateCron(string cronExpression)
    {
        Cron = CronExpression.Parse(cronExpression);
        CronText = cronExpression;
    }

    /// <summary>Next scheduled fire time (UTC) after the last run, or null if none.</summary>
    internal DateTime? NextRunUtc() => Cron.GetNextOccurrence(LastRunUtc, TimeZoneInfo.Utc);

    /// <summary>Due when the next cron occurrence after the last run has passed. A task that was due
    /// while Ari was busy simply stays due (overdue) until an idle window arrives.</summary>
    internal bool IsDue(DateTime nowUtc)
    {
        DateTime? next = Cron.GetNextOccurrence(LastRunUtc, TimeZoneInfo.Utc);
        return next.HasValue && next.Value <= nowUtc;
    }
}
