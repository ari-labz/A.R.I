using ARI.Common;
using Microsoft.Data.Sqlite;

namespace ARI.Calendar;

// Every SQL statement in the calendar lives in this file. CalendarModule calls named methods here;
// it never writes SQL itself — same split as ARI.Brain's Database.cs. Unlike the brain, this database
// IS the source of truth (there are no calendar markdown files to derive it from), so schema creation
// is idempotent (CREATE TABLE IF NOT EXISTS) rather than drop-and-rebuild.
//
// Events and reminders share one table with a `kind` discriminator rather than two tables: both are
// queried the same way (by date, by id, by recurrence), and neither has columns the other couldn't
// simply leave null.
internal static class Database
{
    private const string SCHEMA = """
        CREATE TABLE IF NOT EXISTS entries (
            entryID        INTEGER PRIMARY KEY AUTOINCREMENT,
            kind           TEXT NOT NULL,
            title          TEXT NOT NULL,
            notes          TEXT,
            start          TEXT,
            end            TEXT,
            isWholeDay     INTEGER NOT NULL DEFAULT 0,
            triggerTime    TEXT,
            prompt         TEXT,
            context        TEXT,
            lastFiredUtc   TEXT,
            firedCount     INTEGER NOT NULL DEFAULT 0,
            recurFrequency TEXT NOT NULL DEFAULT 'None',
            recurInterval  INTEGER NOT NULL DEFAULT 1,
            recurDays      TEXT,
            recurUntil     TEXT,
            recurCount     INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_entries_kind ON entries(kind);
        """;

    private const string KIND_EVENT    = "event";
    private const string KIND_REMINDER = "reminder";

    internal static string Path { get; set; } = string.Empty;

    internal static void EnsureSchema()
    {
        using SqliteConnection db = Open();
        Run(db, SCHEMA);
    }

    // ── Writes ───────────────────────────────────────────────────────────────────────

    internal static long InsertEvent(Event calendarEvent)
    {
        using SqliteConnection db = Open();
        Run(db, """
            INSERT INTO entries(kind, title, notes, start, end, isWholeDay, recurFrequency, recurInterval, recurDays, recurUntil, recurCount)
            VALUES ($kind, $title, $notes, $start, $end, $isWholeDay, $recurFrequency, $recurInterval, $recurDays, $recurUntil, $recurCount)
            """,
            ("$kind", KIND_EVENT), ("$title", calendarEvent.Title), ("$notes", Or(calendarEvent.Notes)),
            ("$start", Iso(calendarEvent.Start)), ("$end", Iso(calendarEvent.End)), ("$isWholeDay", calendarEvent.IsWholeDay ? 1 : 0),
            ("$recurFrequency", calendarEvent.Recurrence.Frequency.ToString()), ("$recurInterval", calendarEvent.Recurrence.Interval),
            ("$recurDays", Or(RecurDaysToText(calendarEvent.Recurrence.DaysOfWeek))),
            ("$recurUntil", Or(IsoOrNull(calendarEvent.Recurrence.Until))),
            ("$recurCount", Or(calendarEvent.Recurrence.Count)));
        return LastId(db);
    }

    internal static long InsertReminder(Reminder reminder)
    {
        using SqliteConnection db = Open();
        Run(db, """
            INSERT INTO entries(kind, title, notes, triggerTime, prompt, context, firedCount, recurFrequency, recurInterval, recurDays, recurUntil, recurCount)
            VALUES ($kind, $title, $notes, $triggerTime, $prompt, $context, 0, $recurFrequency, $recurInterval, $recurDays, $recurUntil, $recurCount)
            """,
            ("$kind", KIND_REMINDER), ("$title", reminder.Title), ("$notes", Or(reminder.Notes)),
            ("$triggerTime", Iso(reminder.TriggerTime)), ("$prompt", reminder.Prompt), ("$context", Or(reminder.Context)),
            ("$recurFrequency", reminder.Recurrence.Frequency.ToString()), ("$recurInterval", reminder.Recurrence.Interval),
            ("$recurDays", Or(RecurDaysToText(reminder.Recurrence.DaysOfWeek))),
            ("$recurUntil", Or(IsoOrNull(reminder.Recurrence.Until))),
            ("$recurCount", Or(reminder.Recurrence.Count)));
        return LastId(db);
    }

    // Advances a reminder to its next occurrence (or leaves it be for the caller to delete, if
    // recurrence has run out) after firing.
    internal static void MarkReminderFired(long id, DateTime firedAtUtc, DateTime? nextTriggerTime, int firedCount)
    {
        using SqliteConnection db = Open();
        Run(db, "UPDATE entries SET lastFiredUtc = $fired, firedCount = $count, triggerTime = COALESCE($next, triggerTime) WHERE entryID = $id",
            ("$fired", Iso(firedAtUtc)), ("$count", firedCount), ("$next", Or(IsoOrNull(nextTriggerTime))), ("$id", id));
    }

    internal static void UpdateEvent(Event calendarEvent)
    {
        using SqliteConnection db = Open();
        Run(db, """
            UPDATE entries SET title = $title, notes = $notes, start = $start, end = $end, isWholeDay = $isWholeDay,
                recurFrequency = $recurFrequency, recurInterval = $recurInterval, recurDays = $recurDays, recurUntil = $recurUntil, recurCount = $recurCount
            WHERE entryID = $id AND kind = $kind
            """,
            ("$title", calendarEvent.Title), ("$notes", Or(calendarEvent.Notes)),
            ("$start", Iso(calendarEvent.Start)), ("$end", Iso(calendarEvent.End)), ("$isWholeDay", calendarEvent.IsWholeDay ? 1 : 0),
            ("$recurFrequency", calendarEvent.Recurrence.Frequency.ToString()), ("$recurInterval", calendarEvent.Recurrence.Interval),
            ("$recurDays", Or(RecurDaysToText(calendarEvent.Recurrence.DaysOfWeek))),
            ("$recurUntil", Or(IsoOrNull(calendarEvent.Recurrence.Until))),
            ("$recurCount", Or(calendarEvent.Recurrence.Count)),
            ("$id", calendarEvent.Id), ("$kind", KIND_EVENT));
    }

    internal static void UpdateReminder(Reminder reminder)
    {
        using SqliteConnection db = Open();
        Run(db, """
            UPDATE entries SET title = $title, notes = $notes, triggerTime = $triggerTime, prompt = $prompt, context = $context,
                recurFrequency = $recurFrequency, recurInterval = $recurInterval, recurDays = $recurDays, recurUntil = $recurUntil, recurCount = $recurCount
            WHERE entryID = $id AND kind = $kind
            """,
            ("$title", reminder.Title), ("$notes", Or(reminder.Notes)),
            ("$triggerTime", Iso(reminder.TriggerTime)), ("$prompt", reminder.Prompt), ("$context", Or(reminder.Context)),
            ("$recurFrequency", reminder.Recurrence.Frequency.ToString()), ("$recurInterval", reminder.Recurrence.Interval),
            ("$recurDays", Or(RecurDaysToText(reminder.Recurrence.DaysOfWeek))),
            ("$recurUntil", Or(IsoOrNull(reminder.Recurrence.Until))),
            ("$recurCount", Or(reminder.Recurrence.Count)),
            ("$id", reminder.Id), ("$kind", KIND_REMINDER));
    }

    internal static bool Delete(long id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, "DELETE FROM entries WHERE entryID = $id", new (string, object)[] { ("$id", id) });
        return command.ExecuteNonQuery() > 0;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────

    internal static List<Event> AllEvents() => QueryEvents("SELECT * FROM entries WHERE kind = $kind", ("$kind", KIND_EVENT));

    internal static List<Reminder> AllReminders() => QueryReminders("SELECT * FROM entries WHERE kind = $kind", ("$kind", KIND_REMINDER));

    internal static Event? EventById(long id) =>
        QueryEvents("SELECT * FROM entries WHERE kind = $kind AND entryID = $id", ("$kind", KIND_EVENT), ("$id", id)).FirstOrDefault();

    internal static Reminder? ReminderById(long id) =>
        QueryReminders("SELECT * FROM entries WHERE kind = $kind AND entryID = $id", ("$kind", KIND_REMINDER), ("$id", id)).FirstOrDefault();

    // ADO.NET parameters take DBNull.Value, never a bare C# null.
    private static object Or(object? value) => value ?? DBNull.Value;

    private static SqliteConnection Open()
    {
        SqliteConnection db = new($"Data Source={Path}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = Command(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long LastId(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, (string Name, object Value)[] parameters)
    {
        SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static List<Event> QueryEvents(string sql, params (string Name, object Value)[] parameters)
    {
        List<Event> events = new();
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Event calendarEvent = new(
                title:      reader.GetString(reader.GetOrdinal("title")),
                start:      ParseIso(reader, "start"),
                end:        ParseIso(reader, "end"),
                isWholeDay: reader.GetInt32(reader.GetOrdinal("isWholeDay")) != 0,
                notes:      NullableString(reader, "notes"),
                recurrence: ReadRecurrence(reader))
            { Id = reader.GetInt64(reader.GetOrdinal("entryID")) };
            events.Add(calendarEvent);
        }
        return events;
    }

    private static List<Reminder> QueryReminders(string sql, params (string Name, object Value)[] parameters)
    {
        List<Reminder> reminders = new();
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Reminder reminder = new(
                title:       reader.GetString(reader.GetOrdinal("title")),
                triggerTime: ParseIso(reader, "triggerTime"),
                prompt:      reader.GetString(reader.GetOrdinal("prompt")),
                context:     NullableString(reader, "context"),
                notes:       NullableString(reader, "notes"),
                recurrence:  ReadRecurrence(reader))
            {
                Id           = reader.GetInt64(reader.GetOrdinal("entryID")),
                LastFiredUtc = NullableIso(reader, "lastFiredUtc"),
                FiredCount   = reader.GetInt32(reader.GetOrdinal("firedCount")),
            };
            reminders.Add(reminder);
        }
        return reminders;
    }

    private static RecurrenceRule ReadRecurrence(SqliteDataReader reader) => new()
    {
        Frequency  = Enum.Parse<RecurrenceFrequency>(reader.GetString(reader.GetOrdinal("recurFrequency"))),
        Interval   = reader.GetInt32(reader.GetOrdinal("recurInterval")),
        DaysOfWeek = RecurDaysFromText(NullableString(reader, "recurDays")),
        Until      = NullableIso(reader, "recurUntil"),
        Count      = reader.IsDBNull(reader.GetOrdinal("recurCount")) ? null : reader.GetInt32(reader.GetOrdinal("recurCount")),
    };

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime ParseIso(SqliteDataReader reader, string column) => DateTime.Parse(reader.GetString(reader.GetOrdinal(column)),
        null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTime? NullableIso(SqliteDataReader reader, string column)
    {
        string? text = NullableString(reader, column);
        return text is null ? null : DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string Iso(DateTime value) => value.ToString("O");

    private static string? IsoOrNull(DateTime? value) => value is DateTime d ? Iso(d) : null;

    private static string? RecurDaysToText(IReadOnlyList<DayOfWeek> days) =>
        days.Count == 0 ? null : string.Join(",", days.Select(d => (int)d));

    private static IReadOnlyList<DayOfWeek> RecurDaysFromText(string? text) =>
        string.IsNullOrEmpty(text) ? Array.Empty<DayOfWeek>() : text.Split(',').Select(s => (DayOfWeek)int.Parse(s)).ToList();
}
