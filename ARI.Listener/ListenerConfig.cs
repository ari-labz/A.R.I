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
}
