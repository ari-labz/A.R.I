namespace ARI.VoiceSynthesis;

public class VoiceSynthesisConfig
{
    public bool   Enabled      { get; init; }
    public string StyleTtsPath { get; set; } = "";
    public string VoicesPath   { get; set; } = "";

    // Mutable StyleTTS2 state (venv, per-model training work dirs, the downloaded pretrained
    // checkpoint cache) — never lives under StyleTtsPath, which is install content and may be
    // read-only / replaced wholesale on update. AppDataRoot-based; see ARI.cs.
    public string DataDir { get; set; } = "";
}
