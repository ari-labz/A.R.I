namespace ARI.Voice;

public class VoiceConfig
{
    public bool   Enabled       { get; init; }
    public string DefaultEngine { get; init; } = "StyleTTS2";
    // Per-engine default model name. Empty string means "pick first found".
    public Dictionary<string, string> DefaultModels { get; init; } = new();
}
