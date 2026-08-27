namespace ARI.LLM;

/// <summary>
/// Placeholder registered for Read filesystem tools when no project is bound.
/// Tells the LLM what went wrong and how to fix it, rather than hiding the tool entirely.
/// </summary>
internal sealed class UnboundFilesystemTool(string name) : Tool
{
    internal override string     Name   => name;
    internal override ToolAccess Access => ToolAccess.Read;

    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = name,
            description = $"Read the project filesystem ({name}). Requires a project to be bound first.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson) =>
        Task.FromResult<ToolResult>(
            "No project is bound — call bind_project first, then call this tool again.");
}
