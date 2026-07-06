using System.Text.Json.Serialization;

namespace ARI.Brain;

public class BrainConfig
{
    public bool Enabled { get; init; }

    /// <summary>Root directory of the markdown vault. Empty expands to ~/.ari/Brain at startup.</summary>
    [JsonPropertyName("VaultPath")]
    public string VaultPath { get; set; } = string.Empty;

    /// <summary>Directory where brain backups are written. Relative paths are resolved from the working directory.</summary>
    [JsonPropertyName("BackupPath")]
    public string BackupPath { get; set; } = "./Backups";

    /// <summary>Maximum number of backup files to keep. Oldest is deleted when this limit is exceeded.</summary>
    [JsonPropertyName("MaxBackups")]
    public int MaxBackups { get; set; } = 5;
}
