using ARI.Common;

namespace ARI.Voice;

public interface ITtsSynthesiser : IDisposable
{
    string EngineName { get; }
    Task<bool> CheckHealth();
    Task Start(CancellationToken ct = default);
    Task Warmup(CancellationToken ct = default);
    Task<byte[]> Synthesise(string text, CancellationToken ct = default);
    Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default);
    float Speed { get; }
    float PauseScale { get; }
    void SaveSettings(float speed, float pauseScale);
    IReadOnlyList<EngineParameter> GetParameters();
}
