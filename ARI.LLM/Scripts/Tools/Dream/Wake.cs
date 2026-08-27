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
            description = "End the dream and interrupt your owner with a message. The bar is high — wake only if you have something that genuinely cannot wait: a question whose answer would meaningfully change what you do next, an insight that shifts how you understand an important problem, or something you discovered that your owner needs to know now. Interesting observations, half-formed thoughts, check-ins, or things that can be written to a note and reviewed later do not clear the bar. When in doubt, keep exploring — you have all the time you need and silence is the right default. Content is the message the user will see; Context is a private briefing for your waking self.",
            parameters  = new
            {
                type       = "object",
                required   = new[] { "content" },
                properties = new
                {
                    content = new { type = "string", description = "The message to send to the user." },
                    context = new { type = "string", description = "Private briefing injected into the new thread's system prompt so your waking self already knows the context. The user does not see this." },
                }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args    = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string      content = args.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        string      context = args.TryGetProperty("context", out JsonElement x) ? x.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<ToolResult>("content is required — provide the message to send to your owner.");

        return Task.FromResult(ToolResult.AsWake(content, context));
    }
}
