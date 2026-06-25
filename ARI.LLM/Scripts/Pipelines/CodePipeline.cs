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

        // No project root → nothing to edit; answer as plain chat (no tools).
        if (string.IsNullOrWhiteSpace(localPath))
            return code.SendPrompt(thread, effectivePrompt, username, ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);

        string        resolvedRoot = Path.GetFullPath(localPath);
        FileSnapshots snapshots    = new();

        // Architect path: the CodeArchitect explores + plans on an internal sub-thread, then commissions
        // a Coder per atomic step. It manages the parent's single visible response itself (tool registration
        // happens on the sub-threads), so the whole flow renders as one continuous, JSON-free thread.
        if (architect is not null)
            return architect.Orchestrate(thread, threadKey, effectivePrompt, username, code, resolvedRoot, snapshots, cts, onDelta);

        // Fallback (no CodeArchitect configured): degraded solo Coder with the full edit toolset.
        Shared.Logger.LogWarning("[Code] ({Thread}) CodeArchitect not configured — running solo Coder.", threadKey);
        new PreviewFile(resolvedRoot, cts.Token).Register(thread);
        new ReadFile(resolvedRoot, cts.Token).Register(thread);
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
