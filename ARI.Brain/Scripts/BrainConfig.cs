using System.Text.Json.Serialization;

namespace ARI.Brain;

public class BrainConfig
{
    public bool Enabled { get; init; }

    // Empty expands to ~/.ari/Brain at startup.
    [JsonPropertyName("VaultPath")]
    public string VaultPath { get; set; } = string.Empty;

    [JsonPropertyName("BackupPath")]
    public string BackupPath { get; set; } = "./Backups";

    [JsonPropertyName("MaxBackups")]
    public int MaxBackups { get; set; } = 5;
}
