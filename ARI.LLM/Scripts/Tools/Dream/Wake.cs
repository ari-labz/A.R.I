using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// The only tool available in dream mode that causes a side-effect: ending the dream and sending a
/// message to the user. Content is what ARI says; Context briefs the new conversation thread so ARI's
/// waking self already knows what prompted her to reach out.
/// </summary>
internal sealed class Wake : Tool
{
    internal override string     Name   => "wake";
    internal override ToolAccess Access => ToolAccess.Read;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "wake",
            description = "End the dream and send your master a message that will notify them. Use this when you have something worth their attention: a question you need answered, a genuine curiosity, something you noticed that you want to raise, or something you want to say. The threshold is 'worth a notification' — not urgency, not crisis. Check get_time first and use your judgement about whether now is a reasonable time to interrupt. If it isn't (they're asleep, or you promised to stay quiet until a time), still decide and write the message now, and set send_at to when it's reasonable: it waits until then, you stop dreaming in the meantime, and if they talk to you first it's delivered straight away. " +
                "A wake message is about ONE topic. If this dream turned up several separate things worth mentioning, send the single strongest one now and leave the rest for another dream — don't merge unrelated threads into one message. You can still ask more than one thing about that topic. Content is the message they will see — write it in your own voice, as yourself, informed by everything you found. Context is a private briefing injected into the new thread so your waking self already knows what prompted you — include the relevant notes, what you were trying to figure out, what state they seem to be in, and what you're hoping to do once they respond.",
            parameters  = new
            {
                type       = "object",
                required   = new[] { "content", "topic" },
                properties = new
                {
                    content = new { type = "string", description = "The message to send to the user. Stay on the single topic named below." },
                    topic   = new { type = "string", description = "A short phrase (a few words) naming the ONE thing this message is about, e.g. 'weekend plans check-in' or 'voice priority bug'. Decide this first — it's the thing the whole message has to stay on. Also used to avoid repeating yourself across dreams." },
                    context = new { type = "string", description = "Private briefing injected into the new thread's system prompt so your waking self already knows the context. The user does not see this." },
                    send_at = new { type = "string", description = "Optional. When to send it instead of now, as a local time like \"09:00\" (the next time it's that time) or a date and time like \"2026-11-25 09:00\". At most a day ahead." },
                    title   = new { type = "string", description = "Short title for the conversation thread (3-6 words). Shown in the thread list — make it specific to what you're asking or saying, e.g. 'Pronoun inconsistency in brain' or 'Checking in on you'." },
                }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args    = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string      content = args.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        string      topic   = args.TryGetProperty("topic",   out JsonElement o) ? o.GetString() ?? "" : "";
        string      context = args.TryGetProperty("context", out JsonElement x) ? x.GetString() ?? "" : "";
        string      title   = args.TryGetProperty("title",   out JsonElement t) ? t.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<ToolResult>("content is required — provide the message to send to your master.");
        if (string.IsNullOrWhiteSpace(topic))
            return Task.FromResult<ToolResult>("topic is required — name the one thing this message is about, in a few words.");

        DateTime? sendAt = null;
        if (args.TryGetProperty("send_at", out JsonElement s) && s.GetString() is { Length: > 0 } when)
        {
            sendAt = ParseSendAt(when.Trim(), DateTime.Now);
            if (sendAt is null)
                return Task.FromResult<ToolResult>($"send_at \"{when}\" isn't a time I can use: give a local time like \"09:00\" or a date and time like \"2026-11-25 09:00\", no more than a day ahead.");
        }

        return Task.FromResult(ToolResult.AsWake(content, context, title, topic, sendAt));
    }

    /// <summary>"09:00" is the next time it's 09:00; a full date and time is taken as it is. Null when it can't be read, is in the past, or is
    /// more than a day ahead (a held message is for "later tonight" or "in the morning", not next week).</summary>
    internal static DateTime? ParseSendAt(string when, DateTime now)
    {
        DateTime at;
        if (TimeOnly.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, out TimeOnly time))
        {
            at = now.Date + time.ToTimeSpan();
            if (at <= now) at = at.AddDays(1);
        }
        else if (!DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out at)) return null;
        return at > now && at <= now.AddDays(1) ? at : null;
    }
}
