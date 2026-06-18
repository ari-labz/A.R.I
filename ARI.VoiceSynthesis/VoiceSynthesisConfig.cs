namespace ARI.VoiceSynthesis;

public class VoiceSynthesisConfig
{
    public bool Enabled { get; init; }
    public string StyleTtsPath { get; set; } = "";
    public string VoicesPath   { get; set; } = "";
}
