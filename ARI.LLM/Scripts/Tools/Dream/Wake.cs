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
            description = "End the dream and send your owner a message that will notify them. Use this when you have something worth their attention: a question you need answered, a genuine curiosity, something you noticed that you want to raise, or something you want to say. The threshold is 'worth a notification' — not urgency, not crisis. Check get_time first and use your judgement about whether now is a reasonable time to interrupt. Content is the message they will see — write it in your own voice, as yourself, informed by everything you found. Context is a private briefing injected into the new thread so your waking self already knows what prompted you — include the relevant notes, what you were trying to figure out, what state they seem to be in, and what you're hoping to do once they respond.",
            parameters  = new
            {
                type       = "object",
                required   = new[] { "content" },
                properties = new
                {
                    content = new { type = "string", description = "The message to send to the user." },
                    context = new { type = "string", description = "Private briefing injected into the new thread's system prompt so your waking self already knows the context. The user does not see this." },
                    title   = new { type = "string", description = "Short title for the conversation thread (3-6 words). Shown in the thread list — make it specific to what you're asking or saying, e.g. 'Pronoun inconsistency in brain' or 'Checking in on you'." },
                }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args    = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string      content = args.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        string      context = args.TryGetProperty("context", out JsonElement x) ? x.GetString() ?? "" : "";
        string      title   = args.TryGetProperty("title",   out JsonElement t) ? t.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<ToolResult>("content is required — provide the message to send to your owner.");

        return Task.FromResult(ToolResult.AsWake(content, context, title));
    }
}
