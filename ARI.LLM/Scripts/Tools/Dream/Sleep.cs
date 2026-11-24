using System.Text.Json;

namespace ARI.LLM;

/// <summary>Lets a dream go quiet until a set time and then carry on, with a note to itself about what to pick up. Nothing runs while she
/// sleeps (the GPU rests too), and talking to her ends the sleep early.</summary>
internal sealed class Sleep : Tool
{
    /// <summary>The sleep she asked for in the dream that is ending, picked up by the dream orchestrator. Null when she didn't ask.</summary>
    internal static (DateTime Until, string Note)? Requested { get; set; }

    internal override string     Name   => "sleep";
    internal override ToolAccess Access => ToolAccess.Read;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "sleep",
            description = "Go quiet until a set time, then carry on dreaming. Use it when there's nothing worth doing right now but there will be later " +
                "(you're waiting for the morning, or for something to change), instead of re-reading what you've already read. The dream ends when you " +
                "call it; at that time a new dream starts with your note. Your master talking to you ends the sleep early.",
            parameters  = new
            {
                type       = "object",
                required   = new[] { "until" },
                properties = new
                {
                    until = new { type = "string", description = "When to carry on: a local time like \"07:30\" (the next time it's that time) or a date and time. At most a day ahead." },
                    note  = new { type = "string", description = "A note to yourself for when you carry on: what you were thinking about and what to do next." },
                }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args  = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string      until = args.TryGetProperty("until", out JsonElement u) ? u.GetString() ?? "" : "";
        string      note  = args.TryGetProperty("note",  out JsonElement n) ? n.GetString() ?? "" : "";
        if (Wake.ParseSendAt(until.Trim(), DateTime.Now) is not { } at)
            return Task.FromResult<ToolResult>($"until \"{until}\" isn't a time I can use: give a local time like \"07:30\" or a date and time, no more than a day ahead.");
        Requested = (at, note.Trim());
        return Task.FromResult<ToolResult>($"Sleeping until {at:HH:mm}. Write a line to close the dream, without calling another tool.");
    }
}
