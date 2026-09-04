using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public delegate Task<ITtsSynthesiser?> SynthesiserFactory(string engine, string modelName);

public class VoiceModule : IVoiceModule, IDisposable
{
    private ITtsSynthesiser synthesiser;
    private SpeechQueue     queue;
    private readonly SynthesiserFactory factory;
    private readonly Action<SpeechQueue> wireAudio;
    private readonly ILogger? logger;
    private readonly object   switchLock = new();

    public string? ActiveModel  { get; private set; }
    public string  ActiveEngine => synthesiser.EngineName;
    public bool    IsReady      => synthesiser.CheckHealth().GetAwaiter().GetResult();

    public VoiceModule(
        ITtsSynthesiser synthesiser,
        SpeechQueue queue,
        string modelName,
        SynthesiserFactory factory,
        Action<SpeechQueue> wireAudio,
        ILogger? logger = null)
    {
        this.synthesiser = synthesiser;
        this.queue       = queue;
        this.factory     = factory;
        this.wireAudio   = wireAudio;
        this.logger      = logger;
        ActiveModel      = modelName;
    }

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
        => synthesiser.Synthesise(text, ct);

    public Task<byte[]> Synthesise(string text, Dictionary<string, object>? engineParams, CancellationToken ct = default)
        => synthesiser.Synthesise(text, engineParams, ct);

    public (float speed, float pauseScale) GetVoiceSettings() => (synthesiser.Speed, synthesiser.PauseScale);
    public void SetVoiceSettings(float speed, float pauseScale) => synthesiser.SaveSettings(speed, pauseScale);

    public void Speak(string text) => queue.Enqueue(text);

    public IReadOnlyList<EngineParameter> GetEngineParameters() => synthesiser.GetParameters();

    public ITtsSynthesiser Synthesiser => synthesiser;

    public async Task SwitchEngine(string engine, string modelName, CancellationToken ct = default)
    {
        ITtsSynthesiser? newSynth = await factory(engine, modelName)
            ?? throw new InvalidOperationException($"Could not create {engine} synthesiser for model '{modelName}'.");

        logger?.LogInformation("[Voice] Switching to {Engine} / {Model}...", engine, modelName);

        await newSynth.Start(ct);
        try { await newSynth.Warmup(ct); }
        catch (Exception ex) { logger?.LogWarning("[Voice] Warmup failed after switch: {Error}", ex.Message); }

        ITtsSynthesiser oldSynth;
        SpeechQueue oldQueue;
        lock (switchLock)
        {
            oldSynth    = synthesiser;
            oldQueue    = queue;
            synthesiser = newSynth;
            queue       = new SpeechQueue(newSynth, logger);
            wireAudio(queue);
            ActiveModel = modelName;
        }

        oldQueue.Dispose();
        oldSynth.Dispose();

        logger?.LogInformation("[Voice] Now using {Engine} / {Model}.", engine, modelName);
    }

    public void Dispose()
    {
        queue.Dispose();
        synthesiser.Dispose();
    }
}
