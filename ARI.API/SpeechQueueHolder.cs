using ARI.Voice;

namespace ARI.API;

public class SpeechQueueHolder
{
    private StyleTtsSynthesiser? synthesiser;
    private SpeechQueue?         queue;

    public bool   IsReady     => synthesiser != null;
    public string ActiveModel { get; private set; } = "";

    public void Set(StyleTtsSynthesiser stt, SpeechQueue speechQueue, string modelName)
    {
        synthesiser = stt;
        queue       = speechQueue;
        ActiveModel = modelName;
    }

    public Task<byte[]> Synthesise(string text, CancellationToken ct = default)
    {
        if (synthesiser == null) throw new InvalidOperationException("Voice module is not running.");
        return synthesiser.Speak(text, ct);
    }

    public void Speak(string text) => queue?.Enqueue(text);
}
