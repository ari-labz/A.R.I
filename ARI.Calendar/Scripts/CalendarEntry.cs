namespace ARI.Calendar;

/// <summary>Something ARI's calendar knows about. An Event is context fed to her; a Reminder is a
/// prompt she acts on. Both can repeat, so recurrence lives here rather than on either subclass.</summary>
public abstract class CalendarEntry
{
    public long Id { get; internal set; }
    public string Title { get; set; }
    public string? Notes { get; set; }
    public RecurrenceRule Recurrence { get; set; }

    protected CalendarEntry(string title, string? notes, RecurrenceRule recurrence)
    {
        Title      = title;
        Notes      = notes;
        Recurrence = recurrence;
    }
}
