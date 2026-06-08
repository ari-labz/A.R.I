using ARI.Voice;

namespace ARI.WebPanel;

public class SpeechQueueHolder
{
    private SpeechQueue? queue;

    public bool   IsReady     => queue != null;
    public string ActiveModel { get; private set; } = "";

    public void Set(SpeechQueue speechQueue, string modelName)
    {
        queue       = speechQueue;
        ActiveModel = modelName;
    }

    public void Speak(string text) => queue?.Enqueue(text);
}
