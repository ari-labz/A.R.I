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
            if (wake.SendAt is { } at && at > DateTime.Now)
            {
                Shared.Logger.LogInformation("[Dream] ({Thread}) Wake decided — holding it until {At:HH:mm}.", threadKey, at);
                HeldWake.Hold(new HeldWake(wake.Text, wake.Context, wake.Title, wake.Topic, at));
            }
            else
            {
                Shared.Logger.LogInformation("[Dream] ({Thread}) Wake called — opening proactive thread.", threadKey);
                Send(wake.Text, wake.Context, wake.Title, wake.Topic);
            }
        }

        return result;
    }

    /// <summary>Sends a held wake message once its time has come, or straight away with now (their master is back). True if one was sent.</summary>
    internal bool SendHeldWake(bool now)
    {
        if (HeldWake.Current is not { } held || (!now && held.SendAt > DateTime.Now)) return false;
        if (HeldWake.Take() is not { } taken) return false;
        Shared.Logger.LogInformation("[Dream] Sending the wake message held until {At:HH:mm}.", taken.SendAt);
        Send(taken.Content, taken.Context, taken.Title, taken.Topic);
        return true;
    }

    private void Send(string content, string context, string title, string topic)
    {
        onWake(content, context, title);
        WakeHistory.Record(topic, title);
    }

    // Excluded from dreams, along with every plugin's tools: no external side-effects.
    private static readonly HashSet<string> DreamExcluded =
        ["edit_memory",
         // Reading how tools are written only leads to the owner-only authoring tools, which a dream can't use.
         "tool_docs", "read_tool", "list_my_tools"];

    /// <summary>
    /// Registers the always-available Read tools for a dream turn. Filesystem tools are NOT
    /// registered here — they unlock in the same step that bind_project succeeds, via
    /// ToolFactories.RegisterFilesystemTools. Wake is always registered regardless of access level.
    /// </summary>
    internal static void RegisterDreamTools(Thread thread)
    {
        foreach (string name in ToolFactories.AllNames())
            if (!DreamExcluded.Contains(name) && !PluginTools.IsProvided(name) &&
                ToolFactories.TryBuild(name, thread, out Tool tool) && tool.Access == ToolAccess.Read)
                tool.Register(thread);

        new Wake().Register(thread);
        new Sleep().Register(thread);
    }
}
