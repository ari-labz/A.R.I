using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Calendar;

/// <summary>
/// ARI's calendar: events (context fed to her) and reminders (prompts she acts on), both scalable
/// through SQLite rather than an in-memory list — see Database.cs. Owns its own reminder-check loop
/// (call Start() once at startup) — there is no separate scheduler module to ride.
/// </summary>
public sealed class CalendarModule : ICalendarModule, IDisposable
{
    // Walking a recurrence forward one occurrence at a time is cheap pure arithmetic (no I/O), so a
    // generous cap costs nothing while still bounding a pathological rule (e.g. daily, set up a decade
    // ago) to a fixed amount of work per query.
    private const int MAX_OCCURRENCES_SCANNED = 3660;
    private static readonly TimeSpan REMINDER_POLL_INTERVAL = TimeSpan.FromMinutes(1);

    private readonly ILogger logger;
    private CancellationTokenSource? loopCts;

    public CalendarModule(CalendarConfig config, string persistentDataDir, ILogger logger)
    {
        this.logger = logger;
        Database.Path = Path.Combine(persistentDataDir, "Calendar.db");
        Directory.CreateDirectory(persistentDataDir);
        Database.EnsureSchema();
    }

    /// <summary>Starts the reminder-check loop. Every minute is cheap (a handful of SQLite rows) and
    /// keeps a reminder's actual firing time within a minute of what it was scheduled for.</summary>
    public void Start()
    {
        loopCts = new CancellationTokenSource();
        _ = ReminderLoopAsync(loopCts.Token);
    }

    private async Task ReminderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckDueReminders(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "[Calendar] Reminder loop error."); }

            try { await Task.Delay(REMINDER_POLL_INTERVAL, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        loopCts?.Cancel();
        loopCts?.Dispose();
    }

    // ── ICalendarModule ──────────────────────────────────────────────────────────────

    public long CreateEvent(string title, DateTime start, DateTime end, bool isWholeDay, string? notes, RecurrenceInfo? recurrence)
    {
        Event calendarEvent = new(title, start, end, isWholeDay, notes, RecurrenceRule.FromInfo(recurrence));
        long id = Database.InsertEvent(calendarEvent);
        logger.LogInformation("[Calendar] Created event '{Title}' ({Start:u} - {End:u}).", title, start, end);
        return id;
    }

    public long CreateReminder(string title, DateTime triggerTime, string prompt, string? context, string? notes, RecurrenceInfo? recurrence)
    {
        Reminder reminder = new(title, triggerTime, prompt, context, notes, RecurrenceRule.FromInfo(recurrence));
        long id = Database.InsertReminder(reminder);
        logger.LogInformation("[Calendar] Created reminder '{Title}' for {TriggerTime:u}.", title, triggerTime);
        return id;
    }

    public IReadOnlyList<CalendarEventInfo> ListEvents(int days)
    {
        (DateTime rangeStart, DateTime rangeEnd) = TodayWindow(days);
        return ListEventsInRange(rangeStart, rangeEnd);
    }

    public IReadOnlyList<ReminderInfo> ListReminders(int days)
    {
        (DateTime rangeStart, DateTime rangeEnd) = TodayWindow(days);
        return ListRemindersInRange(rangeStart, rangeEnd);
    }

    public IReadOnlyList<CalendarEventInfo> ListEventsInRange(DateTime start, DateTime end)
    {
        List<CalendarEventInfo> results = new();
        foreach (Event calendarEvent in Database.AllEvents())
            results.AddRange(OccurrencesInRange(calendarEvent, start, end));
        return results.OrderBy(e => e.Start).ToList();
    }

    public IReadOnlyList<ReminderInfo> ListRemindersInRange(DateTime start, DateTime end) =>
        Database.AllReminders()
            .Where(r => r.TriggerTime >= start && r.TriggerTime < end)
            .OrderBy(r => r.TriggerTime)
            .Select(ToInfo)
            .ToList();

    private static (DateTime Start, DateTime End) TodayWindow(int days)
    {
        DateTime today = DateTime.Now.Date;
        return days >= 0 ? (today, today.AddDays(days)) : (today.AddDays(days), today);
    }

    public CalendarEventInfo? GetEvent(long id)
    {
        Event? calendarEvent = Database.EventById(id);
        return calendarEvent is null ? null : ToInfo(calendarEvent, calendarEvent.Start, calendarEvent.End);
    }

    public ReminderInfo? GetReminder(long id)
    {
        Reminder? reminder = Database.ReminderById(id);
        return reminder is null ? null : ToInfo(reminder);
    }

    public bool UpdateEvent(long id, string title, DateTime start, DateTime end, bool isWholeDay, string? notes, RecurrenceInfo? recurrence)
    {
        Event? existing = Database.EventById(id);
        if (existing is null) return false;
        Event updated = new(title, start, end, isWholeDay, notes, RecurrenceRule.FromInfo(recurrence)) { Id = id };
        Database.UpdateEvent(updated);
        return true;
    }

    public bool UpdateReminder(long id, string title, DateTime triggerTime, string prompt, string? context, string? notes, RecurrenceInfo? recurrence)
    {
        Reminder? existing = Database.ReminderById(id);
        if (existing is null) return false;
        Reminder updated = new(title, triggerTime, prompt, context, notes, RecurrenceRule.FromInfo(recurrence))
            { Id = id, LastFired = existing.LastFired, FiredCount = existing.FiredCount };
        Database.UpdateReminder(updated);
        return true;
    }

    public bool DeleteEntry(long id) => Database.Delete(id);

    // ── Context injection ────────────────────────────────────────────────────────────

    /// <summary>Events active today, formatted for the system prompt. Call sites still need to wire
    /// this into wherever ARI's prompt is assembled — it is not injected automatically.</summary>
    public string GetActiveContext(DateTime day)
    {
        IReadOnlyList<CalendarEventInfo> today = ListEventsInRange(day.Date, day.Date.AddDays(1));
        if (today.Count == 0) return string.Empty;

        IEnumerable<string> lines = today.Select(e => e.IsWholeDay
            ? $"- {e.Title} (all day)"
            : $"- {e.Title} ({e.Start:HH:mm}-{e.End:HH:mm})");
        return "Today's calendar:\n" + string.Join('\n', lines);
    }

    // ── Reminder firing ──────────────────────────────────────────────────────────────

    /// <summary>Checks every reminder for a due occurrence and fires it through a real agent turn.
    /// Called from the reminder loop started by Start(); public so it can also be triggered manually.</summary>
    public async Task CheckDueReminders(CancellationToken ct)
    {
        List<Reminder> due = Database.AllReminders().Where(r => r.IsDue(DateTime.Now)).ToList();
        foreach (Reminder reminder in due)
        {
            if (ct.IsCancellationRequested) break;
            try { await FireReminder(reminder, ct); }
            catch (Exception ex) { logger.LogError(ex, "[Calendar] Reminder '{Title}' failed to fire.", reminder.Title); }
        }
    }

    private async Task FireReminder(Reminder reminder, CancellationToken ct)
    {
        if (Modules.Llm is null)
        {
            logger.LogWarning("[Calendar] Reminder '{Title}' is due but the LLM module isn't up yet — leaving it due.", reminder.Title);
            return;
        }

        await Modules.Llm.FireReminderAsync(reminder.Prompt, reminder.Context ?? string.Empty, reminder.Title, ct);

        int firedCount = reminder.FiredCount + 1;
        DateTime? next = reminder.Recurrence.NextOccurrence(reminder.TriggerTime, reminder.TriggerTime, firedCount);
        if (next is null)
            Database.Delete(reminder.Id);
        else
            Database.MarkReminderFired(reminder.Id, DateTime.Now, next, firedCount);

        logger.LogInformation("[Calendar] Reminder '{Title}' fired{Next}.", reminder.Title,
            next is DateTime n ? $"; next occurrence {n:u}" : " (final occurrence)");
    }

    // ── Recurrence expansion ─────────────────────────────────────────────────────────

    private static IEnumerable<CalendarEventInfo> OccurrencesInRange(Event calendarEvent, DateTime rangeStart, DateTime rangeEnd)
    {
        TimeSpan duration = calendarEvent.End - calendarEvent.Start;
        DateTime occurrenceStart = calendarEvent.Start;

        if (!calendarEvent.Recurrence.Repeats)
        {
            if (Overlaps(occurrenceStart, occurrenceStart + duration, rangeStart, rangeEnd))
                yield return ToInfo(calendarEvent, occurrenceStart, occurrenceStart + duration);
            yield break;
        }

        int occurrenceNumber = 1;
        while (occurrenceNumber <= MAX_OCCURRENCES_SCANNED && occurrenceStart < rangeEnd)
        {
            DateTime occurrenceEnd = occurrenceStart + duration;
            if (Overlaps(occurrenceStart, occurrenceEnd, rangeStart, rangeEnd))
                yield return ToInfo(calendarEvent, occurrenceStart, occurrenceEnd);

            DateTime? next = calendarEvent.Recurrence.NextOccurrence(occurrenceStart, occurrenceStart, occurrenceNumber);
            if (next is null) yield break;
            occurrenceStart = next.Value;
            occurrenceNumber++;
        }
    }

    private static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && aEnd >= bStart;

    private static CalendarEventInfo ToInfo(Event calendarEvent, DateTime start, DateTime end) =>
        new(calendarEvent.Id, calendarEvent.Title, calendarEvent.Notes, start, end, calendarEvent.IsWholeDay, calendarEvent.Recurrence.ToInfo());

    private static ReminderInfo ToInfo(Reminder reminder) =>
        new(reminder.Id, reminder.Title, reminder.Notes, reminder.TriggerTime, reminder.Prompt, reminder.Context, reminder.Recurrence.ToInfo());
}
