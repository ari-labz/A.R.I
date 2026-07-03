using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ARI.Listener;

/// <summary>
/// Owns the Python Whisper worker process (whisper_serve.py) that does VAD segmentation + transcription.
/// Mirrors the ARI.Voice StyleTTS2 subprocess pattern: spawn a local service, talk to it over a socket.
/// </summary>
public sealed class WhisperWorker : IDisposable
{
    private readonly ListenerConfig config;
    private readonly ILogger? logger;
    private Process? proc;

    public WhisperWorker(ListenerConfig config, ILogger? logger = null)
    {
        this.config = config;
        this.logger = logger;
    }

    public string WebSocketUrl => $"ws://127.0.0.1:{config.WhisperPort}/ws";
    public bool Running => proc is { HasExited: false };

    /// <summary>Best-effort launch. Returns false (and logs) if the script/interpreter is unavailable —
    /// the Listener still accepts connections but reports transcription as unavailable.</summary>
    public bool Start()
    {
        if (string.IsNullOrWhiteSpace(config.ScriptPath) || !File.Exists(config.ScriptPath))
        {
            logger?.LogWarning("[Listener] whisper_serve.py not found at '{Path}' — transcription disabled.", config.ScriptPath);
            return false;
        }

        try
        {
            ProcessStartInfo info = new()
            {
                FileName               = config.PythonPath,
                Arguments              = $"\"{config.ScriptPath}\" --port {config.WhisperPort} --model {config.Model}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            proc = Process.Start(info);
            if (proc is null) { logger?.LogError("[Listener] Failed to start whisper worker."); return false; }

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) logger?.LogInformation("[Whisper] {Line}", e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) logger?.LogInformation("[Whisper] {Line}", e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            logger?.LogInformation("[Listener] Whisper worker started (pid {Pid}) on port {Port}, model {Model}.", proc.Id, config.WhisperPort, config.Model);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[Listener] Whisper worker failed to launch.");
            return false;
        }
    }

    public void Dispose()
    {
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        proc?.Dispose();
        proc = null;
    }
}
