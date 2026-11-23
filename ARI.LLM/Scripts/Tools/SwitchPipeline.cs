using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Moves the whole conversation onto another pipeline: today, from Dialogue to Code. Routing is deterministic (a bound
/// git project pins the thread to Code); this is the way to switch when a coding request lands on a Dialogue thread
/// anyway, with or without a project bound. The tool only flags the thread. <see cref="LLMModule"/> moves it once this
/// dialogue turn has finished, so she gets to say one line to the user first, and the full history comes with it.
/// Nothing ever moves a thread back by itself. Registered on Dialogue threads only.
/// </summary>
internal sealed class SwitchPipeline : Tool
{
    private readonly Thread thread;
    internal SwitchPipeline(Thread thread) => this.thread = thread;

    internal override string     Name   => "switch_pipeline";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "switch_pipeline",
            description = "Move this whole conversation to the Code pipeline, history included, when the user wants code written, changed, " +
                          "fixed, built or tested: a refactor, a bug fix, a new feature, running a build. Do NOT call it for questions " +
                          "about code, explaining or reading files, or planning chat: answer those yourself. It works with or without a " +
                          "project bound. Once switched, the thread stays on Code.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    pipeline = new { type = "string", @enum = new[] { "code" }, description = "The pipeline to move to." },
                    brief    = new { type = "string", description = "One or two sentences: what the user wants done, plus anything from this conversation the coder needs." },
                },
                required = new[] { "pipeline" },
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        // A desktop-attached project's file tools live on the client's websocket and don't survive the thread being
        // rebuilt as a Code thread; the user switches those with the pipeline selector instead.
        if (thread.HasClientTools)
            return Task.FromResult<ToolResult>("This conversation's project lives on the user's desktop app, which I can't switch from here. " +
                "Tell the user to switch this thread to Code with the pipeline selector.");

        string brief = "";
        try { brief = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.TryGetProperty("brief", out JsonElement b) ? b.GetString() ?? "" : ""; }
        catch { }

        thread.PendingCoderHandoff = brief;
        return Task.FromResult<ToolResult>("[System: switching pipeline: this thread moves to Code the moment you finish this reply, with the whole conversation. " +
            "Say ONE short line to the user, in your own voice, that this is coding work and you're moving it to Code. " +
            "Do not start the work yourself and do not call any more tools.]");
    }
}
