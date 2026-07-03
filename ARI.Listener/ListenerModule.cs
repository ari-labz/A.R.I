using System.Net.WebSockets;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.Listener;

/// <summary>
/// Real-time audio hub. Accepts audio streams (from the browser mic or a client) over WebSocket, has the
/// Whisper worker transcribe them, and runs the fast conversational-awareness gate to decide whether Ari is
/// being addressed. For now the verdict is logged and echoed to the client; committing addressed turns into
/// the Speech pipeline (turn-taking, gaps, interrupts) is the next step.
/// </summary>
public sealed class ListenerModule : IListenerModule, IDisposable
{
    private readonly LLMModule llm;
    private readonly ILogger?  logger;
    private readonly WhisperWorker worker;

    public ListenerModule(LLMModule llm, ListenerConfig config, ILogger? logger = null)
    {
        this.llm    = llm;
        this.logger = logger;
        worker      = new WhisperWorker(config, logger);
    }

    public bool IsReady => worker.Running;

    /// <summary>Launch the Whisper worker. Safe to call once at startup.</summary>
    public bool Start() => worker.Start();

    /// <summary>Handle one accepted browser/client audio WebSocket for its lifetime.</summary>
    public Task HandleConnectionAsync(WebSocket socket, ListenerSessionContext ctx, CancellationToken ct)
    {
        logger?.LogInformation("[Listener] stream opened — source={Source} thread={Thread} user={User}", ctx.Source, ctx.ThreadKey, ctx.UserId ?? "?");
        return new ListenerSession(socket, worker, llm, ctx, logger).RunAsync(ct);
    }

    public void Dispose() => worker.Dispose();
}
