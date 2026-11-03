using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.Listener;

/// <summary>
/// One audio connection: pumps PCM from the browser to the Whisper worker, receives final transcripts,
/// decides whether each was meant for Ari (wake word, follow-up window, or the awareness gate), and echoes
/// {transcript, addressed} back to the browser; {attending} goes out the moment her name is heard.
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
        "You are in a live, spoken voice conversation. Answer in a sentence or two unless they ask for more, easy to say aloud. " +
        "Keep the wit dry and light: one quip at most, only when it fits. Don't guess at who they're with, why they're asking or what they're doing. " +
        "Do not use markdown, lists, headings, or code blocks — plain spoken sentences only. " +
        "You answer straight away, without thinking first. If something needs research, careful reasoning or several tools, " +
        "say in a few words that you're on it and hand it to spawn_agent, then keep talking; you'll get the result and can tell them.";
    // Her spoken name (Customisation → Branding) anywhere in a line wakes her straight away, like a smart speaker's wake
    // word. For the default "Ari", Whisper spells it a few ways, and after "hey" it often blurs into "Hey RE" / "Hey RA",
    // so those count too; a name she's been given counts as written.
    private static Regex WakeWord => wakeWord ??= BuildWakeWord();
    private static Regex? wakeWord;
    static ListenerSession() => AssistantName.Changed += () => wakeWord = null;

    private static Regex BuildWakeWord()
    {
        string spoken = AssistantName.SpokenName;
        if (spoken.Equals(AssistantName.DefaultSpokenName, StringComparison.OrdinalIgnoreCase))
            return new(@"\b(ari|arie|arri|ary|aree)\b|\bhey,?\s+(re|ra|ree|r\.e|harry)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        string words = string.Join(@"\W*", spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        return new($@"(?<!\w){words}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    // The default name's misheard forms, so she's told "Hey Ari" rather than puzzling over "Hey RE(?)".
    private static readonly Regex MisheardName = new(@"\bhey(\(\?\))?,?\s+(re|ra|ree|r\.e|harry)\b[,.]?(\(\?\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static string UnMishear(string text) =>
        AssistantName.SpokenName.Equals(AssistantName.DefaultSpokenName, StringComparison.OrdinalIgnoreCase)
            ? MisheardName.Replace(text, "Hey " + AssistantName.DefaultSpokenName) : text;
    // Right after she speaks, a reply needs no wake word (a smart speaker's follow-up mode). It counts from when her
    // reply is sent, and her audio is still playing for a few seconds after that.
    private static readonly TimeSpan FOLLOW_UP_WINDOW = TimeSpan.FromSeconds(12);
    private DateTime followUpUntil = DateTime.MinValue;
    // While she's thinking or speaking, talking back to her counts as a follow-up too (answering over her).
    // A count, not a flag: a reply over her cancels her answer and starts another, and the two overlap briefly.
    private int responding;
    private bool InFollowUp => Volatile.Read(ref responding) > 0 || DateTime.UtcNow < followUpUntil;
    // When a partial of the line still being spoken had her name: Whisper's final pass sometimes spells it
    // differently ("Hey RE"), and the line was still meant for her. Lapses if that line never finishes.
    private DateTime wokeAt = DateTime.MinValue;
    private static readonly TimeSpan WAKE_HOLD = TimeSpan.FromSeconds(15);
    private bool WokeThisLine => DateTime.UtcNow - wokeAt < WAKE_HOLD;
    // Likewise a line that began inside the follow-up window stays a follow-up, however long Whisper takes to finish it.
    private DateTime followUpLineAt = DateTime.MinValue;
    private bool FollowUpLine => DateTime.UtcNow - followUpLineAt < WAKE_HOLD;
    // A line without her name may still be someone talking to another person or the TV: she can choose not to answer.
    private const string MaybeNotForYou =
        $"\n[Voice: this didn't use your name and the microphone hears the whole room. If it wasn't meant for you, reply with only {SILENT}.]";
    // What she replies to stay quiet; never spoken.
    private const string SILENT = "[silent]";
    private static readonly Regex SilentReply = new(@"^\W*silent\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        string? type, text, clarity, marked;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            type    = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            text    = doc.RootElement.TryGetProperty("text", out JsonElement x) ? x.GetString() : null;
            clarity = doc.RootElement.TryGetProperty("clarity", out JsonElement c) ? c.GetString() : null;
            marked  = doc.RootElement.TryGetProperty("marked", out JsonElement m) ? m.GetString() : null;
        }
        catch { return; }

        // Forward partial transcripts straight through (ephemeral, no gate).
        // Also feed them into the active steering context so Ari can keep thinking as the user speaks.
        if (type == "partial" && !string.IsNullOrWhiteSpace(text))
        {
            // Her name, or a reply in the follow-up window, shows she's listening the moment it's heard.
            if (WakeWord.IsMatch(text!)) wokeAt = DateTime.UtcNow;
            if (InFollowUp && !FollowUpLine) followUpLineAt = DateTime.UtcNow;
            if (WokeThisLine || FollowUpLine)
                await SendJson(new { type = "attending" }, ct);
            await SendJson(new { type = "partial", text }, ct);
            activeSteering?.AddPartial(text!);
            return;
        }

        if (type != "final" || string.IsNullOrWhiteSpace(text)) return;

        // Final arrived — close the steering context so the model is allowed to respond.
        activeSteering?.Finish();
        activeSteering = null;

        transcript.Add(text!);
        // Her name or the follow-up window answers at once; anything else (the TV, someone else in the room, the user
        // talking to someone) goes through the awareness gate, which judges whether it was meant for her.
        string why = WokeThisLine || WakeWord.IsMatch(text!) ? "wake word" : FollowUpLine || InFollowUp ? "follow-up" : "gate";
        wokeAt = followUpLineAt = DateTime.MinValue;
        bool addressed = why != "gate" || await llm.EvaluateAwareness(text!, BuildAwarenessContext(), ct);
        logger?.LogInformation("[Listener] ({Src}/{User}) {Verdict} ({Why}): \"{Text}\"",
            ctx.Source, ctx.UserId ?? "?", addressed ? "ADDRESSED" : "overheard", why, text);
        await SendJson(new { type = "transcript", text, addressed }, ct);

        if (addressed)
        {
            string heard = UnMishear(text!), heardMarked = marked is null ? null : UnMishear(marked);
            StartResponse(WithClarityNote(heard, clarity, heardMarked) + (why == "wake word" ? "" : MaybeNotForYou), ct);
        }
    }

    // When Whisper was guessing, Ari gets the plain line plus the words it was unsure of, and decides herself whether to
    // answer the gist or ask again. (Marking words inline made her comment on the marks.) Clear turns go through untouched.
    private static readonly Regex GuessedWord = new(@"(\S+?)[,.!?]*\(\?\)", RegexOptions.Compiled);
    private static string WithClarityNote(string text, string? clarity, string? marked)
    {
        if (clarity == "garbled")
            return $"{text}\n[Voice: this came through badly garbled (noise or a cutting-out mic), so most of it is guesswork. Don't act on it; say briefly that you didn't catch it.]";
        if (clarity != "unclear" || marked is null) return text;
        string[] unsure = GuessedWord.Matches(marked).Select(m => m.Groups[1].Value).Distinct().ToArray();
        return unsure.Length == 0 ? text
            : $"{text}\n[Voice: the speech recogniser wasn't sure of: {string.Join(", ", unsure)}. Answer what they most likely meant without mentioning it; ask only if you can't tell.]";
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
        finally { followUpUntil = DateTime.UtcNow + FOLLOW_UP_WINDOW; await SendJson(new { type = "done" }, ct); }
    }

    private async Task RespondAsync(string userText, SpeechSteeringContext steering, CancellationToken ct)
    {
        // Sentences are synthesised on a single ordered pump and the WAV streamed to the browser, which
        // plays it through its own audio session (issue #91). Avoids the host-side CoreAudio device conflict
        // with the open mic (PortAudio -9986).
        Channel<string> sentences = Channel.CreateUnbounded<string>();
        bool spoke = false;
        Interlocked.Increment(ref responding);

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
                if (SilentReply.IsMatch(sentence)) { logger?.LogInformation("[Listener] Ari stayed quiet (not meant for her)."); return; }
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
            // Only a reply she actually said opens the follow-up window; staying quiet sends her back to idle.
            Interlocked.Decrement(ref responding);
            if (spoke) followUpUntil = DateTime.UtcNow + FOLLOW_UP_WINDOW;
            await SendJson(new { type = "done", spoke }, ct);
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
