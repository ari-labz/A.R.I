using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// Coordinates mid-stream steering for the Speech pipeline.
/// ListenerSession feeds Whisper partials in as they arrive; SpeechPipeline passes the
/// callbacks into Agent.Send so the model is redirected back into thinking mode whenever
/// it tries to emit a response while the user is still speaking.
/// </summary>
public sealed class SpeechSteeringContext
{
    private readonly ConcurrentQueue<string> _partials = new();
    private volatile bool _finished;

    /// <summary>Feed the next Whisper partial into the context. Ignored once Finish() is called.</summary>
    public void AddPartial(string text)
    {
        if (!_finished) _partials.Enqueue(text);
    }

    /// <summary>Signal that the Whisper final transcript has arrived — no more partials are expected.</summary>
    public void Finish() => _finished = true;

    /// <summary>True while the user is still speaking (not finished, or partials still queued for injection).</summary>
    internal bool UserStillTalking => !_finished || !_partials.IsEmpty;

    /// <summary>Dequeue the next pending partial for injection, joining all queued ones into one message.</summary>
    internal string? ConsumeAll()
    {
        List<string> parts = new();
        while (_partials.TryDequeue(out string? p)) parts.Add(p);
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
