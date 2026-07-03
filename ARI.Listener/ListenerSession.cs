using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.Listener;

/// <summary>
/// One audio connection: pumps PCM from the browser to the Whisper worker, receives final transcripts,
/// runs the awareness gate on each, and echoes {transcript, addressed} back to the browser.
/// </summary>
internal sealed class ListenerSession
{
    private readonly WebSocket browser;
    private readonly WhisperWorker worker;
    private readonly LLMModule llm;
    private readonly ListenerSessionContext ctx;
    private readonly ILogger? logger;
    private readonly List<string> transcript = new();

    public ListenerSession(WebSocket browser, WhisperWorker worker, LLMModule llm, ListenerSessionContext ctx, ILogger? logger)
    {
        this.browser = browser;
        this.worker  = worker;
        this.llm     = llm;
        this.ctx     = ctx;
        this.logger  = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!worker.Running)
        {
            await SendJson(new { type = "error", message = "transcription unavailable" }, ct);
            await DrainBrowser(ct);
            return;
        }

        // The worker takes time to load the model (up to ~20s on first run while it downloads). Retry with
        // backoff instead of giving up — a ClientWebSocket can't be reused after a failed connect, so build
        // a fresh one each attempt.
        ClientWebSocket? toWhisper = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        await SendJson(new { type = "connecting" }, ct);
        while (toWhisper is null && !ct.IsCancellationRequested && DateTime.UtcNow < deadline && browser.State == WebSocketState.Open)
        {
            ClientWebSocket attempt = new();
            try { await attempt.ConnectAsync(new Uri(worker.WebSocketUrl), ct); toWhisper = attempt; }
            catch { attempt.Dispose(); try { await Task.Delay(750, ct); } catch { break; } }
        }

        if (toWhisper is null)
        {
            logger?.LogWarning("[Listener] whisper worker not reachable after retries; transcription unavailable.");
            await SendJson(new { type = "error", message = "transcription unavailable" }, ct);
            await DrainBrowser(ct);
            return;
        }

        using (toWhisper)
        {
            logger?.LogInformation("[Listener] connected to whisper worker.");
            await SendJson(new { type = "ready" }, ct);

            Task up   = PumpBrowserToWhisper(toWhisper, ct);
            Task down = PumpWhisperToBrowser(toWhisper, ct);
            await Task.WhenAny(up, down);

            try { await toWhisper.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { /* ignore */ }
        }
    }

    // Browser PCM (binary) → Whisper worker. Text frames from the browser are treated as control and forwarded.
    private async Task PumpBrowserToWhisper(ClientWebSocket toWhisper, CancellationToken ct)
    {
        byte[] buf = new byte[16 * 1024];
        while (browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await browser.ReceiveAsync(buf, ct); } catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.Count == 0) continue;
            WebSocketMessageType type = r.MessageType == WebSocketMessageType.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
            try { await toWhisper.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), type, r.EndOfMessage, ct); } catch { break; }
        }
    }

    // Whisper transcripts (text/JSON) → awareness gate → browser.
    private async Task PumpWhisperToBrowser(ClientWebSocket toWhisper, CancellationToken ct)
    {
        byte[] buf = new byte[64 * 1024];
        StringBuilder sb = new();
        while (toWhisper.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await toWhisper.ReceiveAsync(buf, ct); } catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType != WebSocketMessageType.Text) continue;
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;
            string msg = sb.ToString(); sb.Clear();
            await HandleWhisperMessage(msg, ct);
        }
    }

    private async Task HandleWhisperMessage(string json, CancellationToken ct)
    {
        string? type, text;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            text = doc.RootElement.TryGetProperty("text", out JsonElement x) ? x.GetString() : null;
        }
        catch { return; }

        // Forward partial transcripts straight through (ephemeral, no gate).
        if (type == "partial" && !string.IsNullOrWhiteSpace(text))
        {
            await SendJson(new { type = "partial", text }, ct);
            return;
        }

        if (type != "final" || string.IsNullOrWhiteSpace(text)) return;

        transcript.Add(text!);
        bool addressed = await llm.EvaluateAwareness(text!, BuildAwarenessContext(), ct);
        logger?.LogInformation("[Listener] ({Src}/{User}) {Verdict}: \"{Text}\"",
            ctx.Source, ctx.UserId ?? "?", addressed ? "ADDRESSED" : "overheard", text);
        await SendJson(new { type = "transcript", text, addressed }, ct);
    }

    // A little rolling context (speaker + recent lines) to help the gate judge cross-talk vs. direct address.
    private string BuildAwarenessContext()
    {
        List<string> recent = transcript.Count > 4 ? transcript.GetRange(transcript.Count - 4, 4) : transcript;
        string who = string.IsNullOrEmpty(ctx.UserId) ? "" : $"Speaker: {ctx.UserId}. ";
        return recent.Count > 1 ? $"{who}Recent lines: {string.Join(" / ", recent)}" : who.TrimEnd();
    }

    private async Task SendJson(object payload, CancellationToken ct)
    {
        if (browser.State != WebSocketState.Open) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try { await browser.SendAsync(bytes, WebSocketMessageType.Text, true, ct); } catch { /* ignore */ }
    }

    private async Task DrainBrowser(CancellationToken ct)
    {
        byte[] buf = new byte[4096];
        while (browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try { WebSocketReceiveResult r = await browser.ReceiveAsync(buf, ct); if (r.MessageType == WebSocketMessageType.Close) break; }
            catch { break; }
        }
    }
}
