using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ARI.VoiceSynthesis;

/// <summary>
/// Generates speech via Piper TTS and then converts the voice using the running RVC instance.
/// </summary>
public class Talk : IDisposable
{
    private const string GradioBase = "http://localhost:7865";

    private readonly string voicesPath;
    private readonly string modelName;
    private readonly string piperModelPath;
    private readonly int pitchShift;
    private readonly ILogger? logger;
    private readonly HttpClient http;

    private string? loadedModel;

    public Talk(
        string voicesPath,
        string modelName,
        string piperModelPath,
        int pitchShift  = 0,
        ILogger? logger = null)
    {
        this.voicesPath     = voicesPath;
        this.modelName      = modelName;
        this.piperModelPath = piperModelPath;
        this.pitchShift     = pitchShift;
        this.logger         = logger;
        this.http           = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>
    /// Synthesises <paramref name="text"/> via Piper and converts the voice via RVC.
    /// Returns raw WAV bytes.
    /// </summary>
    public async Task<byte[]> SpeakAsync(string text, CancellationToken ct = default)
    {
        string pthPath   = Path.Combine(voicesPath, $"{modelName}.pth");
        string indexPath = Path.Combine(voicesPath, $"{modelName}.index");

        if (!File.Exists(pthPath))
            throw new FileNotFoundException($"Voice model not found: {pthPath}");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException($"Voice index not found: {indexPath}");

        string rawWav = Path.Combine(Path.GetTempPath(), $"ari_tts_{Guid.NewGuid():N}.wav");
        string outWav = Path.Combine(Path.GetTempPath(), $"ari_rvc_{Guid.NewGuid():N}.wav");

        try
        {
            // 1 — TTS via Piper
            await SynthesisePiper(text, rawWav, ct);

            // 2 — Load voice model in RVC (only when it changes)
            await EnsureModelLoaded(ct);

            // 3 — Convert via RVC Gradio API
            await ConvertVoice(rawWav, outWav, pthPath, indexPath, ct);

            return await File.ReadAllBytesAsync(outWav, ct);
        }
        finally
        {
            TryDelete(rawWav);
            TryDelete(outWav);
        }
    }

    // ── Piper TTS ─────────────────────────────────────────────────────────────

    private async Task SynthesisePiper(string text, string outputWav, CancellationToken ct)
    {
        Log($"Piper TTS: {text[..Math.Min(text.Length, 60)]}…");

        // Piper reads from stdin and writes a WAV to --output_file
        var psi = new ProcessStartInfo
        {
            FileName               = "piper",
            Arguments              = $"--model \"{piperModelPath}\" --output_file \"{outputWav}\"",
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new Exception("Failed to start piper. Is it installed and on PATH?");

        await proc.StandardInput.WriteLineAsync(text);
        proc.StandardInput.Close();

        string err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception($"Piper failed (exit {proc.ExitCode}): {err}");

        if (!File.Exists(outputWav))
            throw new FileNotFoundException($"Piper produced no output at {outputWav}");
    }

    // ── RVC inference ─────────────────────────────────────────────────────────

    private async Task EnsureModelLoaded(CancellationToken ct)
    {
        string modelFile = $"{modelName}.pth";
        if (loadedModel == modelFile) return;

        Log($"Loading voice model: {modelFile}");

        // infer_change_voice expects just the filename (RVC lists files from weight_root)
        var payload = new { data = new object[] { modelFile } };
        await GradioPredict("infer_change_voice", payload, ct);

        loadedModel = modelFile;
    }

    private async Task ConvertVoice(
        string inputWav, string outputWav,
        string pthPath, string indexPath,
        CancellationToken ct)
    {
        Log("Converting voice via RVC...");

        // Upload WAV to Gradio file endpoint
        string gradioRef = await UploadFile(inputWav, ct);

        // infer_convert (vc_single) parameter order matches the Gradio button click
        var payload = new
        {
            data = new object[]
            {
                0,                  // spk_item: speaker id
                new { name = Path.GetFileName(inputWav), data = default(string), is_file = true, orig_name = Path.GetFileName(inputWav), tmp_path = gradioRef },
                pitchShift,         // vc_transform0: semitone shift
                (string?)null,      // f0_file: no custom F0
                "rmvpe",            // f0method0
                indexPath,          // file_index1: path to .index
                "",                 // file_index2: dropdown (unused)
                0.75,               // index_rate1
                3,                  // filter_radius0
                0,                  // resample_sr0 (0 = no resample)
                0.25,               // rms_mix_rate0
                0.33,               // protect0
            }
        };

        var result = await GradioPredict("infer_convert", payload, ct);

        // Result data[1] is the audio component — Gradio returns { name, data, ... }
        using var doc = JsonDocument.Parse(result);
        string? audioRef = doc.RootElement
            .GetProperty("data")[1]
            .GetProperty("name")
            .GetString();

        if (string.IsNullOrEmpty(audioRef))
            throw new Exception("RVC returned no audio output. Check the RVC log for errors.");

        // Download the converted file from Gradio's /file= endpoint
        byte[] bytes = await http.GetByteArrayAsync($"{GradioBase}/file={audioRef}", ct);
        await File.WriteAllBytesAsync(outputWav, bytes, ct);
    }

    private async Task<string> UploadFile(string filePath, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileBytes  = new ByteArrayContent(await File.ReadAllBytesAsync(filePath, ct));
        fileBytes.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileBytes, "files", Path.GetFileName(filePath));

        var resp = await http.PostAsync($"{GradioBase}/upload", form, ct);
        resp.EnsureSuccessStatusCode();

        string json  = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement[0].GetString()
            ?? throw new Exception("Gradio /upload returned empty path.");
    }

    private async Task<string> GradioPredict(string apiName, object payload, CancellationToken ct)
    {
        string json    = JsonSerializer.Serialize(payload);
        var content    = new StringContent(json, Encoding.UTF8, "application/json");
        var resp       = await http.PostAsync($"{GradioBase}/api/{apiName}/predict", content, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"Gradio /{apiName} returned {resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }

        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private void Log(string message) =>
        logger?.LogInformation("[Talk/{Model}] {Message}", modelName, message);

    public void Dispose() => http.Dispose();
}
