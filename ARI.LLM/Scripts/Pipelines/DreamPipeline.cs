using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class DreamPipeline : Pipeline
{
    private readonly Dreamer                  dreamer;
    private readonly Func<string, string, string> onWake; // (content, context) → new threadKey

    protected override Agent  PrimaryAgent => dreamer;
    protected override string PipelineName => "Dream";

    internal DreamPipeline(
        Dreamer                                                dreamer,
        Func<string, string, string>                           onWake,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.dreamer = dreamer;
        this.onWake  = onWake;
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Dream", threadKey, 0, dreamer.BudgetResponse, dreamer.BudgetContext, 0);

    protected override async Task<string> RunAsync(
        Thread                  thread,
        string                  threadKey,
        string                  effectivePrompt,
        string                  username,
        string?                 platformContext,
        Func<string, Task>?     onDelta,
        CancellationTokenSource cts,
        string?                 localPath,
        Func<string, Task>?     onTextDelta = null)
    {
        RegisterDreamTools(thread);

        Shared.Logger.LogInformation("[Dream] ({Thread}) starting dream turn.", threadKey);

        // Pass anchor as ModeNudge (role: "system") so ARI treats it as her own context,
        // not as a user message she should respond to.
        string result = await dreamer.Prompt(thread, "", new PromptOptions
        {
            Ct        = cts.Token,
            OnDelta   = onDelta,
            ModeNudge = platformContext,
        });

        if (dreamer.WakeRequest is { Kind: ToolResult.ContentKind.Wake } wake)
        {
            Shared.Logger.LogInformation("[Dream] ({Thread}) Wake called — opening proactive thread.", threadKey);
            onWake(wake.Text, wake.Context);
        }

        return result;
    }

    // Read filesystem tools that require a bound project. When no project is bound, stubs are
    // registered in their place so the LLM can see and call them — and get a clear redirect.
    private static readonly string[] FilesystemReadTools =
        ["read_file", "list_directory", "preview_file", "search_files", "find_files", "search_vault"];

    // Read tools excluded from dreams — external/communication tools that don't belong here.
    private static readonly HashSet<string> DreamExcluded =
        ["discord_list_voice_channels"];

    internal static void RegisterDreamTools(Thread thread)
    {
        // Register all Read tools that resolve for this thread.
        HashSet<string> registered = [];
        foreach (string name in ToolFactories.AllNames())
            if (!DreamExcluded.Contains(name) &&
                ToolFactories.TryBuild(name, thread, out Tool tool) && tool.Access == ToolAccess.Read)
            {
                tool.Register(thread);
                registered.Add(name);
            }

        // For filesystem Read tools that didn't resolve (no project bound yet), register stubs
        // so the LLM knows they exist and gets a clear error if it calls them too early.
        foreach (string name in FilesystemReadTools)
            if (!registered.Contains(name))
                new UnboundFilesystemTool(name).Register(thread);

        new Wake().Register(thread);
    }
}
