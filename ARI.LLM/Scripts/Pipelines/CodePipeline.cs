using System.Collections.Concurrent;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class CodePipeline : Pipeline
{
    private readonly Code code;

    protected override Agent  PrimaryAgent => code;
    protected override string PipelineName => "Code";

    internal CodePipeline(
        Code code,
        ConcurrentDictionary<string, CancellationTokenSource> processingThreads,
        ConcurrentDictionary<string, LiveCallInfo>             liveCalls,
        Action<string>                                          notifyWatchers)
        : base(processingThreads, liveCalls, notifyWatchers)
    {
        this.code = code;
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
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            string resolvedRoot = Path.GetFullPath(localPath);
            FileSnapshots snapshots = new();
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
        }

        Shared.Logger.LogInformation("[Code] ({Thread}) prompt\n\"{Prompt}\"", threadKey, effectivePrompt);

        return code.SendPrompt(
            thread, effectivePrompt, username,
            ct:                 cts.Token,
            userMessagePreadded: true,
            onDelta:            onDelta);
    }
}
