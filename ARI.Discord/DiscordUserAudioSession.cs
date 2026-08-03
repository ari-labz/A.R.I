using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Discord.Audio;
using Discord.Audio.Streams;
using Microsoft.Extensions.Logging;

namespace ARI.Discord;

/// <summary>
/// Manages one user's inbound audio from a Discord voice channel.
///
/// Pipeline:
///   AudioInStream.ReadFrameAsync() → RTPFrame (Opus) → OpusDecodeStream → PcmCaptureSink
///   → VAD gate (RMS amplitude) → resample 48 kHz stereo → 16 kHz mono 16-bit LE
///   → Whisper WebSocket → final transcripts → DiscordVoiceCompositor.AddLine()
///
/// VAD: frames below RmsThreshold are discarded. Once a frame exceeds the threshold the gate
/// opens; it stays open until SilenceEndMs ms of consecutive below-threshold frames, then closes.
/// </summary>
internal sealed class DiscordUserAudioSession : IAsyncDisposable
{
    // Discord sends 48 kHz stereo, 20 ms frames → 960 stereo samples = 1920 int16s = 3840 bytes.
    private const int SampleRateIn    = 48000;
    private const int SampleRateOut   = 16000;
    private const int ChannelsIn      = 2;
    private const int FrameMs         = 20;
    private const int FrameSamplesIn  = SampleRateIn / (1000 / FrameMs); // 960 stereo samples
    private const int FrameBytesIn    = FrameSamplesIn * ChannelsIn * 2; // 3840 bytes per frame
    private const int ResampleRatio   = SampleRateIn / SampleRateOut;    // 3

    // VAD thresholds
    private const double RmsThreshold = 200.0;  // ~-44 dBFS for 16-bit — tune if needed
    private const int    SilenceEndMs = 300;     // ms of quiet before gate closes

    private readonly string userId;
    private readonly AudioInStream opusStream;
    private readonly string whisperUrl;
    private readonly DiscordVoiceCompositor compositor;
    private readonly ILogger? logger;
    private readonly CancellationTokenSource cts = new();

    internal DiscordUserAudioSession(
        string userId,
        AudioInStream opusStream,
        string whisperUrl,
        DiscordVoiceCompositor compositor,
        ILogger? logger)
    {
        this.userId      = userId;
        this.opusStream  = opusStream;
        this.whisperUrl  = whisperUrl;
        this.compositor  = compositor;
        this.logger      = logger;
    }

    internal void Start() => _ = Task.Run(() => RunAsync(cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        logger?.LogInformation("[VoiceSession:{UserId}] starting.", userId);
        try { await ConnectAndPumpAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger?.LogWarning(ex, "[VoiceSession:{UserId}] session error.", userId); }
        logger?.LogInformation("[VoiceSession:{UserId}] ended.", userId);
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        // Use a separate timeout CTS for the connection phase so that a DAVE E2EE stream rebuild
        // (which cancels the session's main ct) doesn't abort mid-connect. Bail immediately if ct
        // is already cancelled (handles the rare race where Dispose is called before Start runs).
        if (ct.IsCancellationRequested) return;

        ClientWebSocket? ws = null;
        using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(30));
        while (ws is null && !connectTimeout.IsCancellationRequested)
        {
            ClientWebSocket attempt = new();
            try
            {
                await attempt.ConnectAsync(new Uri(whisperUrl), connectTimeout.Token);
                ws = attempt;
            }
            catch
            {
                attempt.Dispose();
                try { await Task.Delay(750, connectTimeout.Token); } catch { break; }
            }
        }
        if (ws is null) { logger?.LogWarning("[VoiceSession:{UserId}] could not connect to Whisper.", userId); return; }

        logger?.LogInformation("[VoiceSession:{UserId}] connected to Whisper.", userId);

        // Connected — pump uses the session ct so it stops when the session is disposed.
        using (ws)
        {
            Task pump = PumpAudioAsync(ws, ct);
            Task recv = ReceiveTranscriptsAsync(ws, ct);
            await Task.WhenAny(pump, recv);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        }
    }

    private async Task PumpAudioAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // PcmCaptureSink receives decoded PCM from OpusDecodeStream and queues it for the VAD loop.
        PcmCaptureSink sink    = new();
        using OpusDecodeStream decoder = new(sink);

        // Decode loop: read Opus RTP frames, decode them into the sink channel.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    RTPFrame frame = await opusStream.ReadFrameAsync(ct);
                    // Empty payload = DTX silence marker — opus_decode(non-null, len=0) returns BadArg.
                    // Treat as silence and skip; the VAD gate will handle the resulting gap naturally.
                    if (frame.Payload.Length == 0) continue;
                    decoder.WriteHeader(frame.Sequence, frame.Timestamp, frame.Missed);
                    try { await decoder.WriteAsync(frame.Payload, 0, frame.Payload.Length, ct); }
                    catch (InvalidDataException) { /* DAVE E2EE: malformed/transitional frame — skip */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger?.LogWarning(ex, "[VoiceSession:{UserId}] decode error.", userId); }
            finally { sink.Complete(); }
        }, ct);

        // VAD + resample loop: consume decoded PCM, filter silence, send to Whisper.
        byte[] outBuf    = new byte[FrameSamplesIn / ResampleRatio * 2]; // 16 kHz mono output
        int    silenceMs = 0;
        bool   gateOpen  = false;

        await foreach (byte[] pcm in sink.Reader.ReadAllAsync(ct))
        {
            double rms  = ComputeRms(pcm, pcm.Length);
            bool   loud = rms >= RmsThreshold;

            if (loud)
            {
                if (!gateOpen)
                    logger?.LogInformation("[VoiceSession:{UserId}] VAD gate open (rms={Rms:F0}).", userId, rms);
                silenceMs = 0;
                gateOpen  = true;
            }
            else if (!gateOpen)
            {
                continue; // gate already closed — drop frame
            }
            else
            {
                silenceMs += FrameMs;
                if (silenceMs >= SilenceEndMs)
                {
                    gateOpen  = false;
                    silenceMs = 0;
                    continue; // gate just closed — drop frame
                }
                // gate still open but in tail: fall through to send (Whisper needs trailing silence to finalise)
            }

            int outSamples = Resample(pcm, pcm.Length, outBuf);
            if (outSamples == 0) continue;
            try { await ws.SendAsync(new ArraySegment<byte>(outBuf, 0, outSamples * 2), WebSocketMessageType.Binary, true, ct); }
            catch { return; }
        }
    }

    private async Task ReceiveTranscriptsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[]        buf = new byte[64 * 1024];
        StringBuilder sb  = new();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ct); }
            catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (r.MessageType != WebSocketMessageType.Text) continue;

            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;

            string msg = sb.ToString(); sb.Clear();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(msg);
                string? type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
                string? text = doc.RootElement.TryGetProperty("text", out JsonElement x) ? x.GetString() : null;

                if (type == "final" && !string.IsNullOrWhiteSpace(text))
                {
                    logger?.LogInformation("[VoiceSession:{UserId}] transcript: {Text}", userId, text);
                    compositor.AddLine(userId, text!);
                }
            }
            catch { /* malformed JSON — skip */ }
        }
    }

    // 48 kHz stereo 16-bit → 16 kHz mono 16-bit. Averages every ResampleRatio stereo pairs.
    private static int Resample(byte[] inBuf, int inBytes, byte[] outBuf)
    {
        int inSamples   = inBytes / 2;       // total int16 values (L and R interleaved)
        int blockInt16s = ResampleRatio * ChannelsIn;
        int outIdx      = 0;

        for (int i = 0; i + blockInt16s <= inSamples; i += blockInt16s)
        {
            long sum = 0;
            for (int j = 0; j < blockInt16s; j++)
            {
                int byteOff = (i + j) * 2;
                sum += (short)(inBuf[byteOff] | (inBuf[byteOff + 1] << 8));
            }
            short mono = (short)(sum / blockInt16s);
            outBuf[outIdx * 2]     = (byte)(mono & 0xFF);
            outBuf[outIdx * 2 + 1] = (byte)((mono >> 8) & 0xFF);
            outIdx++;
        }
        return outIdx;
    }

    private static double ComputeRms(byte[] buf, int count)
    {
        long sum    = 0;
        int  samples = count / 2;
        for (int i = 0; i < samples; i++)
        {
            int   byteOff = i * 2;
            short s       = (short)(buf[byteOff] | (buf[byteOff + 1] << 8));
            sum += (long)s * s;
        }
        return samples > 0 ? Math.Sqrt((double)sum / samples) : 0;
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        await Task.Delay(50);
        cts.Dispose();
    }

    /// <summary>
    /// Write-only AudioStream sink. Captures decoded PCM chunks from OpusDecodeStream into a
    /// bounded channel for the VAD loop to consume.
    /// </summary>
    private sealed class PcmCaptureSink : AudioStream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

        public ChannelReader<byte[]> Reader => _channel.Reader;

        public override bool CanWrite => true;

        // OpusDecodeStream calls next.WriteHeader before each WriteAsync — accept and ignore it.
        public override void WriteHeader(ushort seq, uint timestamp, bool missed) { }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancelToken)
        {
            byte[] copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _channel.Writer.TryWrite(copy); // non-blocking; drops oldest on overflow
            return Task.CompletedTask;
        }

        public void Complete() => _channel.Writer.TryComplete();
    }
}
