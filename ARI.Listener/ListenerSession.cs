using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ARI.Common;
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
    private CancellationTokenSource? responseCts;
    private SpeechSteeringContext? activeSteering;

    // Steers the model toward short, speakable replies for a live voice conversation.
    private const string SpeechContext =
        "You are in a live, spoken voice conversation. Keep replies concise, natural, and easy to say aloud. " +
        "Do not use markdown, lists, headings, or code blocks — plain spoken sentences only. " +
        "You answer straight away, without thinking first. If something needs research, careful reasoning or several tools, " +
        "say in a few words that you're on it and hand it to spawn_agent, then keep talking; you'll get the result and can tell them.";
    private const int RECONNECT_DELAY_MS = 750;
    private const int WHISPER_CONNECT_TIMEOUT_SEC = 60;
    private const int BROWSER_BUFFER_SIZE_BYTES = 16 * 1024;
    private const int WHISPER_BUFFER_SIZE_BYTES = 64 * 1024;

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
        // Start the voice engine now, while the user is still deciding what to say, so her first reply doesn't
        // wait on Python and the model loading (it stops itself after 10 idle minutes).
        if (Modules.Voice is { } voice)
            _ = Task.Run(async () => { try { await voice.Synthesise("Hi.", ct); } catch { /* the first real sentence will retry */ } }, ct);

        // A background job this conversation started (spawn_agent) wakes the thread when it finishes; say it out
        // loud rather than leaving it in the chat. Only the opening lines are spoken — the full result is on screen.
        Action<string, string> onWoke = (threadKey, reply) =>
        {
            if (threadKey == ctx.ThreadKey && browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
                _ = Task.Run(() => SpeakAsync(reply, ct), ct);
        };
        llm.ThreadWoke += onWoke;
        ct.Register(() => llm.ThreadWoke -= onWoke);

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
        DateTime deadline = DateTime.UtcNow.AddSeconds(WHISPER_CONNECT_TIMEOUT_SEC);
        await SendJson(new { type = "connecting" }, ct);
        while (toWhisper is null && !ct.IsCancellationRequested && DateTime.UtcNow < deadline && browser.State == WebSocketState.Open)
        {
            ClientWebSocket attempt = new();
            try { await attempt.ConnectAsync(new Uri(worker.WebSocketUrl), ct); toWhisper = attempt; }
            catch { attempt.Dispose(); try { await Task.Delay(RECONNECT_DELAY_MS, ct); } catch { break; } }
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

            responseCts?.Cancel(); // stop any in-flight spoken response when the session ends
            try { await toWhisper.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { /* ignore */ }
        }
    }

    // Browser PCM (binary) → Whisper worker. Text frames from the browser are treated as control and forwarded.
    private async Task PumpBrowserToWhisper(ClientWebSocket toWhisper, CancellationToken ct)
    {
        byte[] buf = new byte[BROWSER_BUFFER_SIZE_BYTES];
        // Audio health: how much mic audio arrives and how loud it is, logged a few seconds in and then every 30 s,
        // so "she's stuck listening" can be told apart as no audio, silent audio, or audio Whisper didn't hear as speech.
        long bytes = 0; int peak = 0; DateTime started = DateTime.UtcNow, nextReport = started.AddSeconds(5);
        while (browser.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await browser.ReceiveAsync(buf, ct); } catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.Count == 0) continue;
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                bytes += r.Count;
                for (int i = 0; i + 1 < r.Count; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(buf, i)));
            }
            if (DateTime.UtcNow >= nextReport)
            {
                logger?.LogInformation("[Listener] mic audio: {Kb} KB in {Secs:0}s, peak level {Peak}/32767.", bytes / 1024, (DateTime.UtcNow - started).TotalSeconds, peak);
                peak = 0; nextReport = DateTime.UtcNow.AddSeconds(30);
            }
            WebSocketMessageType type = r.MessageType == WebSocketMessageType.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
            try { await toWhisper.SendAsync(new ArraySegment<byte>(buf, 0, r.Count), type, r.EndOfMessage, ct); } catch { break; }
        }
    }

    // Whisper transcripts (text/JSON) → awareness gate → browser.
    private async Task PumpWhisperToBrowser(ClientWebSocket toWhisper, CancellationToken ct)
    {
        byte[] buf = new byte[WHISPER_BUFFER_SIZE_BYTES];
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
        // Also feed them into the active steering context so Ari can keep thinking as the user speaks.
        if (type == "partial" && !string.IsNullOrWhiteSpace(text))
        {
            await SendJson(new { type = "partial", text }, ct);
            activeSteering?.AddPartial(text!);
            return;
        }

        if (type != "final" || string.IsNullOrWhiteSpace(text)) return;

        // Final arrived — close the steering context so the model is allowed to respond.
        activeSteering?.Finish();
        activeSteering = null;

        transcript.Add(text!);
        // The web orb and the desktop client are one person talking to Ari, so everything is addressed to her;
        // the awareness gate (a model call before anything else can start) is only for shared rooms like Discord.
        bool oneToOne  = ctx.Source is "web" or "client";
        bool addressed = oneToOne || await llm.EvaluateAwareness(text!, BuildAwarenessContext(), ct);
        logger?.LogInformation("[Listener] ({Src}/{User}) {Verdict}: \"{Text}\"",
            ctx.Source, ctx.UserId ?? "?", addressed ? "ADDRESSED" : "overheard", text);
        await SendJson(new { type = "transcript", text, addressed }, ct);

        if (addressed) StartResponse(text!, ct);
    }

    // Run Ari's spoken response to an addressed turn. A new addressed turn cancels the in-flight one.
    private void StartResponse(string transcript, CancellationToken sessionCt)
    {
        responseCts?.Cancel();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        responseCts = cts;

        // Create a fresh steering context. It starts unfinished so the model keeps thinking if
        // further partials arrive before the next final. Finish() is called when the next final lands.
        SpeechSteeringContext steering = new();
        activeSteering = steering;

        _ = Task.Run(() => RespondAsync(transcript, steering, cts.Token), cts.Token);
    }

    private const int SPOKEN_REPORT_MAX_CHARS = 300;

    // Speaks a finished reply that didn't come from a live turn (a woken thread): the first sentences only.
    private async Task SpeakAsync(string reply, CancellationToken ct)
    {
        if (Modules.Voice is null || string.IsNullOrWhiteSpace(reply)) return;
        List<string> chunks = [];
        SentenceChunker chunker = new(chunks.Add);
        chunker.Feed(reply.Length > SPOKEN_REPORT_MAX_CHARS ? reply[..SPOKEN_REPORT_MAX_CHARS] : reply);
        chunker.Flush();
        try
        {
            await SendJson(new { type = "speaking" }, ct);
            int spoken = 0;
            foreach (string chunk in chunks)
            {
                if (spoken > 0 && spoken + chunk.Length > SPOKEN_REPORT_MAX_CHARS) break;
                spoken += chunk.Length;
                await SendJson(new { type = "say", text = chunk }, ct);
                byte[] wav = await Modules.Voice.Synthesise(chunk, ct);
                if (wav.Length > 0 && browser.State == WebSocketState.Open)
                    await browser.SendAsync(wav, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger?.LogWarning(ex, "[Listener] speaking a finished background job failed."); }
        finally { await SendJson(new { type = "done" }, ct); }
    }

    private async Task RespondAsync(string userText, SpeechSteeringContext steering, CancellationToken ct)
    {
        // Sentences are synthesised on a single ordered pump and the WAV streamed to the browser, which
        // plays it through its own audio session (issue #91). Avoids the host-side CoreAudio device conflict
        // with the open mic (PortAudio -9986).
        Channel<string> sentences = Channel.CreateUnbounded<string>();
        bool spoke = false;

        Task synthPump = Task.Run(async () =>
        {
            await foreach (string sentence in sentences.Reader.ReadAllAsync(ct))
            {
                if (Modules.Voice is null) continue;
                try
                {
                    if (!spoke) { spoke = true; await SendJson(new { type = "speaking" }, ct); }
                    byte[] wav = await Modules.Voice.Synthesise(sentence, ct);
                    if (wav.Length > 0 && browser.State == WebSocketState.Open)
                        await browser.SendAsync(wav, WebSocketMessageType.Binary, true, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger?.LogWarning(ex, "[Listener] synthesis failed."); }
            }
        }, ct);

        try
        {
            await SendJson(new { type = "thinking" }, ct);           // reasoning before the first token
            SentenceChunker chunker = new(sentence =>
            {
                logger?.LogInformation("[Listener] Ari: \"{Sentence}\"", sentence);
                _ = SendJson(new { type = "say", text = sentence }, ct); // caption on the orb
                sentences.Writer.TryWrite(sentence);                     // queue for synthesis → browser
            });

            // Mark steering finished here as a safety net — it should already be finished by the time
            // the final transcript triggered this call, but guard against timing edge cases.
            steering.Finish();

            await llm.PromptStreaming(
                ctx.ThreadKey, userText,
                username:        ctx.UserId ?? "user",
                platformContext: SpeechContext,
                onDelta:         _ => Task.CompletedTask,
                ct:              ct,
                steering:        steering,
                onTextDelta:     delta => { chunker.Feed(delta); return Task.CompletedTask; });

            chunker.Flush();
        }
        catch (OperationCanceledException) { /* superseded by a newer turn or session ended */ }
        catch (Exception ex) { logger?.LogWarning(ex, "[Listener] response generation failed."); }
        finally
        {
            sentences.Writer.TryComplete();
            try { await synthPump; } catch { /* ignore */ }
            await SendJson(new { type = "done" }, ct);
        }
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
