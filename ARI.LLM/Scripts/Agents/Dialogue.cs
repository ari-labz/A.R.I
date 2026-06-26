using System.Text.Json.Serialization;

namespace ARI.LLM;

internal class Dialogue : Agent
{
    [JsonPropertyName("shortTermMemoryLimit")] public int? ShortTermMemoryLimit { get; init; }
    [JsonPropertyName("maxImageTokens")]       public int MaxImageTokens        { get; init; }

    internal override int  MemoryLimit      => ShortTermMemoryLimit ?? 0;
    internal override bool SuppressPromptLog => true;

    internal event Action<string>? ThreadBufferFull;
    internal event Action<string>? ThreadBecameInactive;
    internal event Action<string>? ThreadDeleted;

    internal void RaiseThreadBufferFull(string key)     => ThreadBufferFull?.Invoke(key);
    internal void RaiseThreadBecameInactive(string key) => ThreadBecameInactive?.Invoke(key);
    internal void RaiseThreadDeleted(string key)        => ThreadDeleted?.Invoke(key);
}
