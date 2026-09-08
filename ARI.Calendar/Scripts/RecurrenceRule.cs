using ARI.Common;

namespace ARI.Calendar;

/// <summary>
/// How a CalendarEntry repeats. Frequency.None means it never repeats. DaysOfWeek only applies to
/// Weekly recurrence — "every weekday" is Weekly, Interval 1, DaysOfWeek Mon-Fri. Until and Count are
/// independent stop conditions; whichever is hit first ends the recurrence.
/// </summary>
public sealed class RecurrenceRule
{
    private const int WEEKLY_SEARCH_LIMIT_DAYS = 7 * 8;

    public RecurrenceFrequency Frequency { get; init; } = RecurrenceFrequency.None;
    public int Interval { get; init; } = 1;
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = Array.Empty<DayOfWeek>();
    public DateTime? Until { get; init; }
    public int? Count { get; init; }

    public bool Repeats => Frequency != RecurrenceFrequency.None;

    public static RecurrenceRule None => new();

    public static RecurrenceRule FromInfo(RecurrenceInfo? info) => info is null
        ? None
        : new RecurrenceRule
        {
            Frequency  = info.Frequency,
            Interval   = Math.Max(1, info.Interval),
            DaysOfWeek = info.DaysOfWeek ?? Array.Empty<DayOfWeek>(),
            Until      = info.Until,
            Count      = info.Count,
        };

    public RecurrenceInfo ToInfo() => new(Frequency, Interval, DaysOfWeek, Until, Count);

    /// <summary>The first occurrence of anchor's schedule strictly after <paramref name="after"/>, or
    /// null once the rule has run out (Until/Count) or never repeated in the first place.
    /// occurrencesSoFar counts anchor itself as occurrence 1.</summary>
    public DateTime? NextOccurrence(DateTime anchor, DateTime after, int occurrencesSoFar)
    {
        if (!Repeats) return null;
        if (Count is int count && occurrencesSoFar >= count) return null;

        DateTime candidate = Frequency == RecurrenceFrequency.Weekly && DaysOfWeek.Count > 0
            ? NextWeeklyOccurrence(anchor, after)
            : NextSimpleOccurrence(anchor, after);

        if (Until is DateTime until && candidate > until) return null;
        return candidate;
    }

    private DateTime NextSimpleOccurrence(DateTime anchor, DateTime after)
    {
        DateTime candidate = anchor;
        while (candidate <= after)
        {
            candidate = Frequency switch
            {
                RecurrenceFrequency.Daily   => candidate.AddDays(Interval),
                RecurrenceFrequency.Weekly  => candidate.AddDays(7 * Interval),
                RecurrenceFrequency.Monthly => candidate.AddMonths(Interval),
                RecurrenceFrequency.Yearly  => candidate.AddYears(Interval),
                _                           => after.AddTicks(1),
            };
        }
        return candidate;
    }

    // Walks forward day by day looking for the next date whose weekday is in DaysOfWeek. Interval
    // beyond 1 isn't meaningfully definable against an explicit weekday set ("every 2 weeks on Mon/Wed"
    // starting from which week?), so a multi-day-of-week rule always checks every week.
    private DateTime NextWeeklyOccurrence(DateTime anchor, DateTime after)
    {
        DateTime start = after < anchor ? anchor : after;
        for (int offset = 1; offset <= WEEKLY_SEARCH_LIMIT_DAYS; offset++)
        {
            DateTime candidate = start.AddDays(offset);
            if (DaysOfWeek.Contains(candidate.DayOfWeek)) return candidate;
        }
        return start.AddDays(7);
    }
}
