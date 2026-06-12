using System.Text.Json.Serialization;

namespace ARI.Brain;

public class BrainConfig
{
    [JsonPropertyName("TriliumUrl")]
    public string TriliumUrl { get; set; } = "http://localhost:8080";

    [JsonPropertyName("EtapiToken")]
    public string EtapiToken { get; set; } = string.Empty;

    [JsonPropertyName("RootNoteId")]
    public string RootNoteId { get; set; } = "root";

    /// <summary>
    /// Maximum number of note contents held in BrainService's in-memory MRU cache.
    /// Shared by all consumers (Recall, Engram, Refactor) — no separate per-consumer caches.
    /// Most-recently used notes are at the front; when full the oldest is evicted.
    /// Set to 0 to disable caching.
    ///
    /// SCALABILITY NOTE: the title index (noteIdCache) is loaded in full at startup from Trilium.
    /// This is fast at a few hundred notes but will become a slow startup and a large memory
    /// footprint at tens of thousands of nodes. When that becomes a concern, the right move is
    /// to switch from a full-tree load to an on-demand index backed by Trilium's search API —
    /// fetching and caching only the titles of notes that have actually been accessed, with a
    /// periodic or event-driven refresh for newly created notes. The BrainCacheSize limit already
    /// handles the content side of this; the title index will need a parallel TTL-bounded approach.
    /// </summary>
    [JsonPropertyName("BrainPublicUrl")]
    public string BrainPublicUrl { get; set; } = "https://brain.a-r-i.ai";

    [JsonPropertyName("BrainCacheSize")]
    public int BrainCacheSize { get; set; } = 50;

    /// <summary>Directory where brain backups are written. Relative paths are resolved from the working directory.</summary>
    [JsonPropertyName("BackupPath")]
    public string BackupPath { get; set; } = "./Backups";

    /// <summary>Maximum number of backup files to keep. Oldest is deleted when this limit is exceeded. Default 5.</summary>
    [JsonPropertyName("MaxBackups")]
    public int MaxBackups { get; set; } = 5;
}
