using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class CodePipeline : Pipeline
{
    private readonly Coder? coder;

    protected override Agent  PrimaryAgent => coder!;
    protected override string PipelineName => "Code";

    internal CodePipeline(
        Coder? coder,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.coder = coder;
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Code", threadKey, 0, coder?.BudgetResponse ?? 0, coder?.BudgetContext ?? 0, 0);

    protected override Task<string> RunAsync(
        Thread               thread,
        string               threadKey,
        string               effectivePrompt,
        string               username,
        string?              platformContext,
        Func<string, Task>?  onDelta,
        CancellationTokenSource cts,
        string?              localPath)
    {
        Shared.Logger.LogInformation("[Code] ({Thread}) prompt\n\"{Prompt}\"", threadKey, effectivePrompt);

        // Every code request runs the SAME pipeline: appraise → Coder. The Coder edits directly with its own
        // file tools; there is no sub-agent dispatch. What differs is only WHERE the filesystem lives:
        //   • Remote: the client connected a project and registered its own file tools over the websocket
        //     (read_file/edit_file/…), which execute on the CLIENT's machine. The project is not on this disk,
        //     so the Coder drives those forwarded tools; `root` stays the raw client path
        //     (used only to build commands sent back to the client — never touched on this disk).
        //   • Local (eval / co-located): the project is on this server's disk; the Coder binds ServerFileSystem.
        // New user turn: bump the serial so client-side per-turn guardrails (read dedup) reset their scope.
        thread.TurnSerial++;

        // Phase transition driven by the user's verdict on a proposed plan (not the LLM). plan_proposed set
        // PlanProposed and captured the payload while the reads were resident; here we act on the reply:
        //   plan on the table + Approve button ("[approve-plan]")  → Development, build from the payload;
        //   plan on the table + any other reply                    → Planning, revise;
        //   no plan on the table                                   → Planning, a fresh request.
        // "[approve-plan]" is the deterministic signal the Accept & Build button sends. Anything else is feedback.
        bool approve = thread.PlanProposed && effectivePrompt.Trim() == "[approve-plan]";
        // Amend: a plan was on the table and the user replied with anything other than approval — that reply IS
        // their requested change. Flag it so the architect hard-steers this turn to end with a fresh plan_proposed.
        thread.RevisingPlan = thread.PlanProposed && !approve;
        if (thread.PlanProposed)
        {
            thread.Phase        = approve ? CodePhase.Development : CodePhase.Planning;
            thread.PlanProposed = false;
            if (approve) effectivePrompt = "Approved — build the plan from your payload now.";
        }
        else thread.Phase = CodePhase.Planning;

        bool remote = thread.tools.ContainsKey("read_file");

        // No fallback. A local Code thread with nothing bound and no client-sent path used to default
        // to Path.GetFullPath(".") — this SERVER's own working directory, which for a dev run is this
        // very repo. That's a real hazard: a misrouted or unbound thread should never get edit tools
        // pointed at anything, let alone ARI's own source. Refuse instead of guessing.
        if (!remote && string.IsNullOrWhiteSpace(localPath))
        {
            const string refusal = "No project is selected for this conversation, so I have no filesystem to work in. Bind a project first (or open one from the sidebar) before asking me to code.";
            // A plain return string here would NOT surface as a visible message — RunAsync's return
            // value is just handed back up ExecuteAsync, not turned into a Response. Build one directly,
            // same as any completed turn, and stream it so the client sees it immediately.
            Response response = new() { State = State.Complete, Content = ContentBlock.Parse(refusal), Timestamp = DateTime.Now };
            thread.AddItem(response);
            onDelta?.Invoke(refusal);
            return Task.FromResult(refusal);
        }

        string        resolvedRoot = remote ? (localPath ?? "") : Path.GetFullPath(localPath!);
        FileSnapshots snapshots    = new();

        if (coder is null)
            throw new InvalidOperationException("Coder is not configured — the code pipeline cannot run.");

        return coder.RunLoop(thread, threadKey, effectivePrompt, username, resolvedRoot, snapshots, cts, onDelta, remote);
    }
}
