using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class DreamPipeline : Pipeline
{
    private readonly Dreamer                  dreamer;
    private readonly Func<string, string, string, string> onWake; // (content, context, title) → new threadKey

    protected override Agent  PrimaryAgent => dreamer;
    protected override string PipelineName => "Dream";

    internal DreamPipeline(
        Dreamer                                                dreamer,
        Func<string, string, string, string>                   onWake,
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
            onWake(wake.Text, wake.Context, wake.Title);
        }

        return result;
    }

    // Communication tools excluded from dreams — no voice channels, no external side-effects.
    private static readonly HashSet<string> DreamExcluded =
        ["discord_list_voice_channels", "discord_join_voice_channel", "discord_leave_voice_channel",
         "edit_memory"];

    /// <summary>
    /// Registers the always-available Read tools for a dream turn. Filesystem tools are NOT
    /// registered here — they unlock in the same step that bind_project succeeds, via
    /// ToolFactories.RegisterFilesystemTools. Wake is always registered regardless of access level.
    /// </summary>
    internal static void RegisterDreamTools(Thread thread)
    {
        foreach (string name in ToolFactories.AllNames())
            if (!DreamExcluded.Contains(name) &&
                ToolFactories.TryBuild(name, thread, out Tool tool) && tool.Access == ToolAccess.Read)
                tool.Register(thread);

        new Wake().Register(thread);
    }
}
