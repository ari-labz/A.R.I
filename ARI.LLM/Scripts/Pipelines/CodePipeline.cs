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

        // Every new user turn starts in PLANNING. The model itself moves to Development within the turn (via
        // dev_mode) once it has presented a plan and the user has approved — there is no harness approval gate.
        thread.Phase = CodePhase.Planning;

        bool          remote       = thread.tools.ContainsKey("read_file");
        string        resolvedRoot = remote ? (localPath ?? "") : Path.GetFullPath(string.IsNullOrWhiteSpace(localPath) ? "." : localPath);
        FileSnapshots snapshots    = new();

        if (architect is null)
            throw new InvalidOperationException("CodeArchitect is not configured — the code pipeline cannot run.");

        return architect.RunLoop(thread, threadKey, effectivePrompt, username, code, resolvedRoot, snapshots, cts, onDelta, remote);
    }
}
