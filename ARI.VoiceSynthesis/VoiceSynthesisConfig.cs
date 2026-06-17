namespace ARI.VoiceSynthesis;

public class VoiceSynthesisConfig
{
    public bool Enabled { get; init; }
    public string StyleTtsPath { get; init; } = "";
    public string VoicesPath   { get; init; } = "";
}
