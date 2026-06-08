using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class F5Synthesiser(string f5Path, string modelPath, string referenceAudio, ILogger? logger = null) : IDisposable
{
    private const string SERVER_SCRIPT = "serve.py";
    private const int    SERVER_PORT   = 8020;
    private const int    WARMUP_MS     = 3000;

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
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

        server = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start F5-TTS inference server.");

        logger?.LogInformation("F5-TTS inference server starting on port {Port}...", SERVER_PORT);
        await Task.Delay(WARMUP_MS, ct);
        logger?.LogInformation("F5-TTS inference server ready.");
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

    private static void WriteServerScript(string path)
    {
        string script = """
import argparse, io
from flask import Flask, request, Response
from f5_tts.api import F5TTS
import soundfile as sf
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('--model', required=True)
parser.add_argument('--ref_audio', required=True)
parser.add_argument('--port', type=int, default=8020)
args = parser.parse_args()

tts = F5TTS(model_type='F5TTS', ckpt_file=args.model)
app = Flask(__name__)

@app.route('/synthesise', methods=['POST'])
def synthesise():
    text = request.json['text']
    wav, sr, _ = tts.infer(ref_file=args.ref_audio, ref_text='', gen_text=text)
    buf = io.BytesIO()
    sf.write(buf, np.array(wav), sr, format='WAV')
    return Response(buf.getvalue(), mimetype='audio/wav')

app.run(port=args.port)
""";
        File.WriteAllText(path, script);
    }
}
