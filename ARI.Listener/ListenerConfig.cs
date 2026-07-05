namespace ARI.Listener;

/// <summary>Configuration for the audio Listener + Whisper worker (part of AriConfig.modules.Listener).</summary>
public class ListenerConfig
{
    public bool   Enabled     { get; init; }
    /// <summary>Local port the Python Whisper worker listens on (WebSocket).</summary>
    public int    WhisperPort { get; init; } = 8123;
    /// <summary>Python interpreter to run the worker with (ideally a venv that has faster-whisper + deps).</summary>
    public string PythonPath  { get; set; } = "python3";
    /// <summary>Path to whisper_serve.py. Relative paths are resolved against the executable dir by the host.</summary>
    public string ScriptPath  { get; set; } = "";
    /// <summary>faster-whisper model name. Smaller = lower latency (e.g. base.en, small, distil-small.en).</summary>
    public string Model       { get; init; } = "base.en";
    /// <summary>CPU threads for transcription (CTranslate2 is CPU-only, so this is the main speed knob).</summary>
    public int    CpuThreads  { get; init; } = 6;
    /// <summary>Trailing silence (ms) that ends an utterance. Lower = snappier, but splits sentences on pauses.</summary>
    public int    SilenceMs   { get; init; } = 400;
}
