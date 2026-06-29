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
        new("Code", threadKey, 0, code.MaxTokens, code.MaxContextTokens, 0);

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

        // No project root → no filesystem access. Tell the model plainly so it asks for
        // attachments instead of pretending to have read the codebase (#61). The note goes to
        // the model only (augmentedPrompt) — the user still sees just their own message.
        if (string.IsNullOrWhiteSpace(localPath))
            return code.SendPrompt(thread, effectivePrompt, username,
                augmentedPrompt: $"[System: You do not have file access. Ask the user to attach any files needed.]\n\n{effectivePrompt}",
                ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);

        string        resolvedRoot = Path.GetFullPath(localPath);
        FileSnapshots snapshots    = new();

        // Architect path: the CodeArchitect runs ON the main thread — it plans, then commissions a Coder
        // sub-thread per task whose work live-streams back, and approves each task's summary before proceeding.
        // (The older Orchestrate, which ran the architect on a hidden sub-thread, is kept as a fallback.)
        if (architect is not null)
            return architect.RunLoop(thread, threadKey, effectivePrompt, username, code, resolvedRoot, snapshots, cts, onDelta);

        // Fallback (no CodeArchitect configured): degraded solo Coder with the full edit toolset.
        Shared.Logger.LogWarning("[Code] ({Thread}) CodeArchitect not configured — running solo Coder.", threadKey);
        new PreviewFile(resolvedRoot, cts.Token, snapshots).Register(thread);
        new ReadFile(resolvedRoot, cts.Token, snapshots).Register(thread);
        new ListDirectory(resolvedRoot, cts.Token).Register(thread);
        new SearchFiles(resolvedRoot, cts.Token).Register(thread);
        new FindFiles(resolvedRoot, cts.Token).Register(thread);
        new EditFile(resolvedRoot, cts.Token, snapshots).Register(thread);
        new WriteFile(resolvedRoot, cts.Token).Register(thread);
        new RevertFile(resolvedRoot, cts.Token, snapshots).Register(thread);
        new DeleteFile(resolvedRoot, cts.Token).Register(thread);
        new MoveFile(resolvedRoot, cts.Token).Register(thread);
        new UpdateTodos(code, thread).Register(thread);

        return code.SendPrompt(thread, effectivePrompt, username, ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);
    }
}
