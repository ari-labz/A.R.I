using ARI.Common;
using Microsoft.AspNetCore.Mvc;

namespace ARI.API.Controllers;

/// <summary>
/// ARI's calendar. Events and reminders are both "entries" with their own id: list them, and create, read, change
/// or delete one at /entries. An entry's <c>kind</c> ("event" or "reminder") says which it is.
/// </summary>
[Route("api/v1/admin/calendar")]
[ApiController]
public class CalendarController : ControllerBase
{
    public sealed record RecurrenceBody(string Frequency, int Interval = 1, List<string>? DaysOfWeek = null, DateTime? Until = null, int? Count = null);

    /// <summary>One shape for both kinds. Events use start/end/isWholeDay; reminders use triggerTime/prompt/context.
    /// On a change (PATCH) any field left out stays as it was; <c>kind</c> can't be changed.</summary>
    public sealed record EntryBody(
        string? Kind, string? Title, string? Notes, RecurrenceBody? Recurrence,
        DateTime? Start, DateTime? End, bool? IsWholeDay,
        DateTime? TriggerTime, string? Prompt, string? Context);

    /// <summary>Either pass `from`/`to` to browse an explicit window (what the control panel's month/week/day views use),
    /// or `days` to look forward/back from today (the default, 42).</summary>
    [HttpGet]
    public IActionResult List([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] int days = 42)
    {
        if (Modules.Calendar is not { } calendar) return StatusCode(503, new { error = "Calendar is not available." });

        if (from is DateTime start && to is DateTime end)
            return Ok(new
            {
                events    = calendar.ListEventsInRange(start, end).Select(ToJson),
                reminders = calendar.ListRemindersInRange(start, end).Select(ToJson),
            });

        return Ok(new
        {
            events    = calendar.ListEvents(days).Select(ToJson),
            reminders = calendar.ListReminders(days).Select(ToJson),
        });
    }

    [HttpGet("entries/{id:long}")]
    public IActionResult Get(long id)
    {
        if (Modules.Calendar is not { } calendar) return StatusCode(503, new { error = "Calendar is not available." });
        if (calendar.GetEvent(id) is { } e) return Ok(ToJson(e));
        if (calendar.GetReminder(id) is { } r) return Ok(ToJson(r));
        return NotFound(new { error = "No such calendar entry." });
    }

    [HttpPost("entries")]
    public IActionResult Create([FromBody] EntryBody body)
    {
        if (Modules.Calendar is not { } calendar) return StatusCode(503, new { error = "Calendar is not available." });
        if (string.IsNullOrWhiteSpace(body.Title)) return BadRequest(new { error = "title is required." });

        switch (body.Kind?.ToLowerInvariant())
        {
            case "event":
                if (body.Start is null || body.End is null) return BadRequest(new { error = "start and end are required." });
                return Ok(new { id = calendar.CreateEvent(body.Title, body.Start.Value, body.End.Value, body.IsWholeDay ?? false, body.Notes, FromBody(body.Recurrence)) });
            case "reminder":
                if (body.TriggerTime is null) return BadRequest(new { error = "triggerTime is required." });
                if (string.IsNullOrWhiteSpace(body.Prompt)) return BadRequest(new { error = "prompt is required." });
                return Ok(new { id = calendar.CreateReminder(body.Title, body.TriggerTime.Value, body.Prompt, body.Context, body.Notes, FromBody(body.Recurrence)) });
            default:
                return BadRequest(new { error = "kind must be \"event\" or \"reminder\"." });
        }
    }

    [HttpPatch("entries/{id:long}")]
    public IActionResult Change(long id, [FromBody] EntryBody body)
    {
        if (Modules.Calendar is not { } calendar) return StatusCode(503, new { error = "Calendar is not available." });
        if (body.Title is { } t && string.IsNullOrWhiteSpace(t)) return BadRequest(new { error = "title can't be empty." });

        // Recurrence is replaced when sent (frequency "none" removes it) and kept when left out.
        if (calendar.GetEvent(id) is { } e)
        {
            bool ok = calendar.UpdateEvent(id, body.Title ?? e.Title, body.Start ?? e.Start, body.End ?? e.End,
                body.IsWholeDay ?? e.IsWholeDay, body.Notes ?? e.Notes, body.Recurrence is null ? e.Recurrence : FromBody(body.Recurrence));
            return ok ? Ok(new { ok = true }) : NotFound(new { error = "No such calendar entry." });
        }
        if (calendar.GetReminder(id) is { } r)
        {
            if (body.Prompt is { } p && string.IsNullOrWhiteSpace(p)) return BadRequest(new { error = "prompt can't be empty." });
            bool ok = calendar.UpdateReminder(id, body.Title ?? r.Title, body.TriggerTime ?? r.TriggerTime, body.Prompt ?? r.Prompt,
                body.Context ?? r.Context, body.Notes ?? r.Notes, body.Recurrence is null ? r.Recurrence : FromBody(body.Recurrence));
            return ok ? Ok(new { ok = true }) : NotFound(new { error = "No such calendar entry." });
        }
        return NotFound(new { error = "No such calendar entry." });
    }

    [HttpDelete("entries/{id:long}")]
    public IActionResult Delete(long id)
    {
        if (Modules.Calendar is not { } calendar) return StatusCode(503, new { error = "Calendar is not available." });
        return calendar.DeleteEntry(id) ? Ok(new { ok = true }) : NotFound(new { error = "No such calendar entry." });
    }

    private static RecurrenceInfo? FromBody(RecurrenceBody? req)
    {
        if (req is null || string.Equals(req.Frequency, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Enum.TryParse(req.Frequency, true, out RecurrenceFrequency frequency)) return null;
        List<DayOfWeek>? days = req.DaysOfWeek?.Select(d => Enum.Parse<DayOfWeek>(d, true)).ToList();
        return new RecurrenceInfo(frequency, Math.Max(1, req.Interval), days, req.Until, req.Count);
    }

    private static object ToJson(RecurrenceInfo? recurrence) => recurrence is null
        ? new { frequency = "none" }
        : new
        {
            frequency  = recurrence.Frequency.ToString(),
            interval   = recurrence.Interval,
            daysOfWeek = recurrence.DaysOfWeek?.Select(d => d.ToString()),
            until      = recurrence.Until,
            count      = recurrence.Count,
        };

    private static object ToJson(CalendarEventInfo e) => new
    {
        id = e.Id, kind = "event", title = e.Title, notes = e.Notes, start = e.Start, end = e.End,
        isWholeDay = e.IsWholeDay, recurrence = ToJson(e.Recurrence),
    };

    private static object ToJson(ReminderInfo r) => new
    {
        id = r.Id, kind = "reminder", title = r.Title, notes = r.Notes, triggerTime = r.TriggerTime,
        prompt = r.Prompt, context = r.Context, recurrence = ToJson(r.Recurrence),
    };
}
