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

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
        => synthesiser.Speak(text, ct);

    public void Speak(string text) => queue.Enqueue(text);
}
