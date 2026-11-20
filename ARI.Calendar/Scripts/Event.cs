namespace ARI.Calendar;

/// <summary>A calendar event: pure context fed to ARI (working hours, a birthday, a trip). She never
/// acts on one by herself — that's what a Reminder is for.</summary>
public sealed class Event : CalendarEntry
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsWholeDay { get; set; }
    /// <summary>When set, ARI reaches out this many minutes before each start. Null means no reminder.</summary>
    public int? RemindBeforeMinutes { get; set; }

    public Event(string title, DateTime start, DateTime end, bool isWholeDay, string? notes = null, RecurrenceRule? recurrence = null, int? remindBeforeMinutes = null)
        : base(title, notes, recurrence ?? RecurrenceRule.None)
    {
        RemindBeforeMinutes = remindBeforeMinutes;
        Start      = start;
        End        = end;
        IsWholeDay = isWholeDay;
    }

    public bool SpansMultipleDays => Start.Date != End.Date;

    public bool IsActiveOn(DateTime day) => day.Date >= Start.Date && day.Date <= End.Date;
}
