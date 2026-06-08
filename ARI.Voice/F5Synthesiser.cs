using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class F5Synthesiser(string f5Path, string modelPath, string referenceAudio, ILogger? logger = null) : IDisposable
{
    private const string SERVER_SCRIPT       = "serve.py";
    private const int    SERVER_PORT         = 8020;
    private const int    POLL_INTERVAL_MS    = 2000;
    private const int    STARTUP_TIMEOUT_SECS = 120;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? server;

    public async Task Start(CancellationToken ct = default)
    {
        string python = Path.Combine(f5Path, "venv", "bin", "python");
        string script = Path.Combine(f5Path, SERVER_SCRIPT);

        WriteServerScript(script);

        ProcessStartInfo info = new()
        {
            FileName               = python,
            Arguments              = $"\"{script}\" --model \"{modelPath}\" --ref_audio \"{referenceAudio}\" --port {SERVER_PORT}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        info.Environment["PYTHONHASHSEED"] = "0";

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start F5-TTS inference server.");

        _ = Task.Run(() => StreamErrors(server.StandardError), ct);
        _ = Task.Run(() => DrainOutput(server.StandardOutput), ct);

        await WaitUntilReady(ct);
    }

    public async Task Warmup(CancellationToken ct = default)
    {
        await Speak("Ready.", ct);
    }

    public async Task<byte[]> Speak(string text, CancellationToken ct = default)
    {
        string url     = $"http://localhost:{SERVER_PORT}/synthesise";
        string payload = JsonSerializer.Serialize(new { text });

        using StringContent body = new(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await http.PostAsync(url, body, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        http.Dispose();
        try { server?.Kill(entireProcessTree: true); } catch { }
        server?.Dispose();
    }

    private async Task WaitUntilReady(CancellationToken ct)
    {
        string healthUrl  = $"http://localhost:{SERVER_PORT}/health";
        int    elapsedSecs = 0;

        while (elapsedSecs < STARTUP_TIMEOUT_SECS)
        {
            await Task.Delay(POLL_INTERVAL_MS, ct);
            elapsedSecs += POLL_INTERVAL_MS / 1000;

            try
            {
                HttpResponseMessage resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }

            if (server?.HasExited == true)
                throw new InvalidOperationException($"F5-TTS server exited unexpectedly (code {server.ExitCode}).");
        }

        throw new TimeoutException($"F5-TTS server did not become ready within {STARTUP_TIMEOUT_SECS}s.");
    }

    private async Task StreamErrors(System.IO.StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("[transformers]", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("HTTP/1.1")) continue;
            logger?.LogWarning("[F5] {Line}", line);
        }
    }

    private static async Task DrainOutput(System.IO.StreamReader reader)
    {
        while (await reader.ReadLineAsync() != null) { }
    }

    private static void WriteServerScript(string path)
    {
        string script = """
import argparse, io, logging
from flask import Flask, request, Response, jsonify
from f5_tts.api import F5TTS
import soundfile as sf
import numpy as np

logging.getLogger('werkzeug').setLevel(logging.ERROR)

parser = argparse.ArgumentParser()
parser.add_argument('--model',     required=True)
parser.add_argument('--ref_audio', required=True)
parser.add_argument('--port',      type=int, default=8020)
args = parser.parse_args()

tts = F5TTS(model='F5TTS_v1_Base', ckpt_file=args.model)

app = Flask(__name__)

@app.route('/health')
def health():
    return jsonify({'status': 'ok'})

@app.route('/synthesise', methods=['POST'])
def synthesise():
    text = request.json['text']
    wav, sr, _ = tts.infer(ref_file=args.ref_audio, ref_text='', gen_text=text)
    buf = io.BytesIO()
    sf.write(buf, np.array(wav), sr, format='WAV', subtype='PCM_16')
    return Response(buf.getvalue(), mimetype='audio/wav')

app.run(port=args.port)
""";
        File.WriteAllText(path, script);
    }
}
