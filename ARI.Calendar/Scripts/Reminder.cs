namespace ARI.Calendar;

/// <summary>A reminder: when TriggerTime arrives, ARI runs Prompt (briefed by Context) through a real
/// agent turn and reaches out — never a static message, always freshly composed.</summary>
public sealed class Reminder : CalendarEntry
{
    public DateTime TriggerTime { get; set; }
    public string Prompt { get; set; }
    public string? Context { get; set; }

    /// <summary>When this reminder's current occurrence last fired. Null if it has never fired.</summary>
    public DateTime? LastFiredUtc { get; set; }

    /// <summary>How many times this reminder has fired, counting its very first trigger as 1 — the
    /// number RecurrenceRule.Count is measured against.</summary>
    public int FiredCount { get; set; }

    public Reminder(string title, DateTime triggerTime, string prompt, string? context, string? notes = null, RecurrenceRule? recurrence = null)
        : base(title, notes, recurrence ?? RecurrenceRule.None)
    {
        TriggerTime = triggerTime;
        Prompt      = prompt;
        Context     = context;
    }

    public bool IsDue(DateTime nowUtc) => TriggerTime <= nowUtc;
}
