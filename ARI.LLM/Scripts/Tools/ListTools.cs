namespace ARI.LLM;

/// <summary>Lists every deferred tool group and its one-line description (issue #126). Registered on
/// every thread — see LLMModule.GetOrCreateThread — so any agent can discover what request_tools can
/// load, without those groups' full schemas sitting in context until actually requested.</summary>
internal sealed class ListTools : Tool
{
    internal override string Name => "list_tools";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "list_tools",
            description = "List every available tool group and what it's for. Call this before request_tools if you don't already know the group name you need.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<string> Execute(string argsJson) => Task.FromResult(ToolGroups.ManifestText());
}
