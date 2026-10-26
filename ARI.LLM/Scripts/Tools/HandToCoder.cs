using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Dialogue → Coder handoff. The deterministic routing (a bound git project pins the thread to the Code
/// pipeline) catches most coding requests; this is the net for the ones that land on a Dialogue thread anyway.
/// The tool only flags the thread — <see cref="LLMModule"/> moves it onto the Code pipeline once this dialogue
/// turn has finished, so she gets to say one line to the user first. Registered on Dialogue threads only.
/// </summary>
internal sealed class HandToCoder : Tool
{
    private readonly Thread thread;
    internal HandToCoder(Thread thread) => this.thread = thread;

    internal override string     Name   => "hand_to_coder";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "hand_to_coder",
            description = "Pass the user's request to your coder agent. Call it when the user wants code in a bound project changed, " +
                          "fixed, built or tested — a refactor, a bug fix, a new feature, running a build. Do NOT call it for questions " +
                          "about code, explaining or reading files, or planning chat — answer those yourself. Needs a project bound to " +
                          "this conversation.",
            parameters  = new
            {
                type       = "object",
                properties = new { brief = new { type = "string", description = "One or two sentences: what the user wants done, plus anything from this conversation the coder needs." } }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (thread.FilesystemRoot is null)
            return Task.FromResult<ToolResult>("No project is bound to this conversation, so there's nothing for the coder to work on. " +
                "Ask the user which project, then call bind_project (list_projects shows them), and hand off again.");

        // A desktop-attached project's file tools live on the client's websocket and don't survive the thread being
        // rebuilt as a Code thread; the user switches those with the pipeline selector instead.
        if (thread.HasClientTools)
            return Task.FromResult<ToolResult>("This conversation's project lives on the user's desktop app, which I can't hand off from here. " +
                "Tell the user to switch this thread to Code with the pipeline selector.");

        string brief = "";
        try { brief = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.TryGetProperty("brief", out JsonElement b) ? b.GetString() ?? "" : ""; }
        catch { }

        thread.PendingCoderHandoff = brief;
        return Task.FromResult<ToolResult>("[System: handed off — your coder takes this thread the moment you finish this reply. " +
            "Say ONE short line to the user, in your own voice, that this is coding work and you're passing it to your coder. " +
            "Do not start the work yourself and do not call any more tools.]");
    }
}
