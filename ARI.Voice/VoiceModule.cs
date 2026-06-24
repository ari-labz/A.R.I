using ARI.Common;

namespace ARI.Voice;

/// <summary>Active voice synthesis instance. Null if the Voice module is disabled.</summary>
public class VoiceModule : IVoiceModule
{
    private readonly StyleTtsSynthesiser synthesiser;
    private readonly SpeechQueue         queue;

    public string ActiveModel { get; }
    public bool IsReady => true;

    public VoiceModule(StyleTtsSynthesiser synthesiser, SpeechQueue queue, string modelName)
    {
        this.synthesiser = synthesiser;
        this.queue       = queue;
        ActiveModel      = modelName;
    }

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f)
        => synthesiser.Speak(text, ct, diffusionSteps, alpha, beta, embeddingScale);

    public Task<byte[]> SynthesiseWithCheckpoint(string text, string checkpointPath, CancellationToken ct = default, int diffusionSteps = 5, float alpha = 0.3f, float beta = 0.7f, float embeddingScale = 1.0f)
        => synthesiser.SpeakWithCheckpoint(text, checkpointPath, ct, diffusionSteps, alpha, beta, embeddingScale);

    public void Speak(string text) => queue.Enqueue(text);
}
