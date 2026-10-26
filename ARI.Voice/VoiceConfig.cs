namespace ARI.Voice;

public class VoiceConfig
{
    public bool   Enabled       { get; init; }
    public string DefaultEngine { get; init; } = "StyleTTS2";
    // The engine process starts on the first synthesis request and is stopped again after this many idle
    // minutes, freeing its RAM. 0 keeps it running from startup, as before.
    public int    IdleStopMinutes { get; init; } = 10;
    // Per-engine default model name. Empty string means "pick first found".
    public Dictionary<string, string> DefaultModels { get; init; } = new();
}
