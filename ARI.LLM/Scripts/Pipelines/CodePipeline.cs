using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class CodePipeline : Pipeline
{
    private readonly Coder          code;
    private readonly CodeArchitect? architect;

    protected override Agent  PrimaryAgent => code;
    protected override string PipelineName => "Code";

    internal CodePipeline(
        Coder code,
        CodeArchitect? architect,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.code      = code;
        this.architect = architect;
    }

    protected override LiveCallInfo BuildLiveCall(string threadKey) =>
        new("Code", threadKey, 0, code.BudgetResponse, code.BudgetContext, 0);

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

        // Every code request runs the SAME pipeline: appraise → architect → the architect spawns coders. Coders
        // are never invoked directly. What differs is only WHERE the filesystem lives:
        //   • Remote: the client connected a project and registered its own file tools over the websocket
        //     (read_file/edit_file/…), which execute on the CLIENT's machine. The project is not on this disk,
        //     so the architect and its coders drive those forwarded tools; `root` stays the raw client path
        //     (used only to build commands sent back to the client — never touched on this disk).
        //   • Local (eval / co-located): the project is on this server's disk; the architect binds ServerFileSystem.
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

        bool          remote       = thread.tools.ContainsKey("read_file");
        string        resolvedRoot = remote ? (localPath ?? "") : Path.GetFullPath(string.IsNullOrWhiteSpace(localPath) ? "." : localPath);
        FileSnapshots snapshots    = new();

        if (architect is null)
            throw new InvalidOperationException("CodeArchitect is not configured — the code pipeline cannot run.");

        return architect.RunLoop(thread, threadKey, effectivePrompt, username, code, resolvedRoot, snapshots, cts, onDelta, remote);
    }
}
