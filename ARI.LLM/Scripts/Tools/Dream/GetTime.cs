namespace ARI.LLM;

/// <summary>
/// Returns the current local date and time. Useful before deciding to wake — check the time
/// so you don't interrupt your owner in the middle of the night.
/// </summary>
internal sealed class GetTime : Tool
{
    internal override string     Name   => "get_time";
    internal override ToolAccess Access => ToolAccess.Read;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "get_time",
            description = "Returns the current local date and time. Call this before deciding to wake so you can judge whether now is a reasonable time to send a message.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        DateTime now = DateTime.Now;
        return Task.FromResult<ToolResult>(now.ToString("dddd, d MMMM yyyy — HH:mm"));
    }
}
