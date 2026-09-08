using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

// create_event / create_reminder / list_events / delete_entry — reach the calendar only through
// ICalendarModule (ARI.Common), never a direct reference to ARI.Calendar's domain classes. Recurrence
// is passed as flat parameters rather than a nested object, matching every other tool's schema style.

internal static class CalendarRecurrence
{
    internal static RecurrenceInfo? FromArgs(JsonElement root)
    {
        string frequencyText = root.TryGetProperty("repeat", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "none" : "none";
        if (!Enum.TryParse(frequencyText, true, out RecurrenceFrequency frequency) || frequency == RecurrenceFrequency.None)
            return null;

        int interval = root.TryGetProperty("repeat_interval", out JsonElement iv) && iv.ValueKind == JsonValueKind.Number ? Math.Max(1, iv.GetInt32()) : 1;

        List<DayOfWeek> days = new();
        if (root.TryGetProperty("repeat_days", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
            foreach (JsonElement entry in d.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String && Enum.TryParse(entry.GetString(), true, out DayOfWeek day))
                    days.Add(day);

        DateTime? until = root.TryGetProperty("repeat_until", out JsonElement u) && u.ValueKind == JsonValueKind.String
            && DateTime.TryParse(u.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime untilValue) ? untilValue : null;

        int? count = root.TryGetProperty("repeat_count", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;

        return new RecurrenceInfo(frequency, interval, days, until, count);
    }
}

internal sealed class CreateEvent : Tool
{
    internal override string Name => "create_event";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "create_event",
            description = "Add an event to ARI's calendar. Events are pure context — they are never acted on by themselves, just shown to you as background (e.g. working hours, a birthday, a trip). Use create_reminder if you actually need to say something to the owner at a given time.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    title        = new { type = "string", description = "Short name for the event, e.g. \"Work\" or \"Brother's birthday\"." },
                    start        = new { type = "string", description = "ISO date/time the event starts." },
                    end          = new { type = "string", description = "ISO date/time the event ends. Can be a later day than start for a multi-day event." },
                    is_whole_day = new { type = "boolean", description = "True if this is an all-day event — start/end are still required (use midnight for both) but time-of-day is ignored." },
                    notes        = new { type = "string", description = "Optional free-text detail." },
                    repeat          = new { type = "string", @enum = new[] { "none", "daily", "weekly", "monthly", "yearly" }, description = "How this repeats. 'none' (default) never repeats." },
                    repeat_interval = new { type = "integer", description = "Repeat every N units of `repeat` (e.g. 2 with 'weekly' = every 2 weeks). Defaults to 1." },
                    repeat_days     = new { type = "array", items = new { type = "string", @enum = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" } }, description = "Only for 'weekly': which days it lands on, e.g. every weekday = [Monday..Friday]." },
                    repeat_until    = new { type = "string", description = "Optional ISO date/time — the recurrence stops after this point." },
                    repeat_count    = new { type = "integer", description = "Optional — the recurrence stops after this many occurrences." },
                },
                required = new[] { "title", "start", "end" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Calendar is not { } calendar) return Task.FromResult<ToolResult>("The calendar isn't available right now.");
        JsonElement root = ToolArgs.Parse(argsJson);

        string title = ToolArgs.Str(root, "title");
        if (title.Length == 0) return Task.FromResult<ToolResult>("Error: 'title' is required.");
        if (!ToolArgs.TryDateTime(root, "start", out DateTime start)) return Task.FromResult<ToolResult>("Error: 'start' must be a valid ISO date/time.");
        if (!ToolArgs.TryDateTime(root, "end", out DateTime end)) return Task.FromResult<ToolResult>("Error: 'end' must be a valid ISO date/time.");
        bool isWholeDay = root.TryGetProperty("is_whole_day", out JsonElement w) && w.ValueKind == JsonValueKind.True;
        string? notes = root.TryGetProperty("notes", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        long id = calendar.CreateEvent(title, start, end, isWholeDay, notes, CalendarRecurrence.FromArgs(root));
        return Task.FromResult<ToolResult>($"Created event '{title}' [{id}] ({start:g} - {end:g}).");
    }
}

internal sealed class CreateReminder : Tool
{
    internal override string Name => "create_reminder";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "create_reminder",
            description = "Schedule a reminder. When trigger_time arrives, ARI runs `prompt` through a real turn (briefed by `context`) and reaches out to the owner — it is not a canned message, so write `prompt` as an instruction to your future self, not as the final text to say.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    title        = new { type = "string", description = "Short name for the reminder, e.g. \"Check garage for SSDs\"." },
                    trigger_time = new { type = "string", description = "ISO date/time this reminder fires." },
                    prompt       = new { type = "string", description = "Instruction to your future self for what to say/do when this fires, e.g. \"Remind Xywren to check the garage for his SSDs now that he's home from work.\"" },
                    context      = new { type = "string", description = "Optional private briefing to have on hand when it fires — background the owner doesn't need to see verbatim." },
                    notes        = new { type = "string", description = "Optional free-text detail, not shown to the owner." },
                    repeat          = new { type = "string", @enum = new[] { "none", "daily", "weekly", "monthly", "yearly" }, description = "How this repeats. 'none' (default) never repeats." },
                    repeat_interval = new { type = "integer", description = "Repeat every N units of `repeat` (e.g. 2 with 'weekly' = every 2 weeks). Defaults to 1." },
                    repeat_days     = new { type = "array", items = new { type = "string", @enum = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" } }, description = "Only for 'weekly': which days it lands on, e.g. every weekday = [Monday..Friday]." },
                    repeat_until    = new { type = "string", description = "Optional ISO date/time — the recurrence stops after this point." },
                    repeat_count    = new { type = "integer", description = "Optional — the recurrence stops after this many occurrences." },
                },
                required = new[] { "title", "trigger_time", "prompt" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Calendar is not { } calendar) return Task.FromResult<ToolResult>("The calendar isn't available right now.");
        JsonElement root = ToolArgs.Parse(argsJson);

        string title = ToolArgs.Str(root, "title");
        string prompt = ToolArgs.Str(root, "prompt");
        if (title.Length == 0) return Task.FromResult<ToolResult>("Error: 'title' is required.");
        if (prompt.Length == 0) return Task.FromResult<ToolResult>("Error: 'prompt' is required.");
        if (!ToolArgs.TryDateTime(root, "trigger_time", out DateTime triggerTime)) return Task.FromResult<ToolResult>("Error: 'trigger_time' must be a valid ISO date/time.");
        string? context = root.TryGetProperty("context", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        string? notes = root.TryGetProperty("notes", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        long id = calendar.CreateReminder(title, triggerTime, prompt, context, notes, CalendarRecurrence.FromArgs(root));
        return Task.FromResult<ToolResult>($"Created reminder '{title}' [{id}] for {triggerTime:g}.");
    }
}

internal sealed class ListEvents : Tool
{
    internal override string     Name   => "list_events";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "list_events",
            description = "List calendar events in a window around today. Positive days looks forward (e.g. 7 = the next week), negative looks backward (e.g. -7 = the last week).",
            parameters  = new
            {
                type       = "object",
                properties = new { days = new { type = "integer", description = "How many days to look, and in which direction (sign). Required." } },
                required   = new[] { "days" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Calendar is not { } calendar) return Task.FromResult<ToolResult>("The calendar isn't available right now.");
        JsonElement root = ToolArgs.Parse(argsJson);
        if (!root.TryGetProperty("days", out JsonElement d) || d.ValueKind != JsonValueKind.Number)
            return Task.FromResult<ToolResult>("Error: 'days' is required.");

        IReadOnlyList<CalendarEventInfo> events = calendar.ListEvents(d.GetInt32());
        if (events.Count == 0) return Task.FromResult<ToolResult>("No events in that window.");

        IEnumerable<string> lines = events.Select(e => e.IsWholeDay
            ? $"[{e.Id}] {e.Title} — {e.Start:d} (all day)"
            : $"[{e.Id}] {e.Title} — {e.Start:g} to {e.End:g}");
        return Task.FromResult<ToolResult>(string.Join("\n", lines));
    }
}

internal sealed class DeleteEntry : Tool
{
    internal override string Name => "delete_entry";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "delete_entry",
            description = "Permanently delete a calendar event or reminder by id (from list_events, or the id returned by create_event/create_reminder).",
            parameters  = new
            {
                type       = "object",
                properties = new { id = new { type = "integer", description = "The entry's id." } },
                required   = new[] { "id" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Calendar is not { } calendar) return Task.FromResult<ToolResult>("The calendar isn't available right now.");
        JsonElement root = ToolArgs.Parse(argsJson);
        if (!root.TryGetProperty("id", out JsonElement i) || i.ValueKind != JsonValueKind.Number)
            return Task.FromResult<ToolResult>("Error: 'id' is required.");

        return Task.FromResult<ToolResult>(calendar.DeleteEntry(i.GetInt64()) ? "Deleted." : "No entry with that id.");
    }
}

internal static class ToolArgs
{
    internal static JsonElement Parse(string argsJson)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    internal static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback).Trim() : fallback;

    internal static bool TryDateTime(JsonElement el, string prop, out DateTime value)
    {
        value = default;
        return el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out value);
    }
}
