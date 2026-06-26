using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ARI.Voice;

public class SpeechQueue : IDisposable
{
    public event Action<byte[]>? AudioReady;

    private readonly StyleTtsSynthesiser synthesiser;
    private readonly Channel<string> queue;
    private readonly CancellationTokenSource cts;
    private readonly ILogger? logger;
    private readonly Task worker;

    public SpeechQueue(StyleTtsSynthesiser synthesiser, ILogger? logger = null)
    {
        this.synthesiser = synthesiser;
        this.logger      = logger;
        this.queue       = Channel.CreateUnbounded<string>();
        this.cts         = new CancellationTokenSource();
        this.worker      = Task.Run(Process);
    }

    public void Enqueue(string text) => queue.Writer.TryWrite(text);

    public void Dispose()
    {
        cts.Cancel();
        queue.Writer.Complete();
        try { worker.Wait(); }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
        cts.Dispose();
    }

    private async Task Process()
    {
        await foreach (string text in queue.Reader.ReadAllAsync(cts.Token))
        {
            try
            {
                byte[] audio = await synthesiser.Speak(text, cts.Token);
                AudioReady?.Invoke(audio);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogError("[Voice] Synthesis failed: {Error}", ex.Message);
            }
        }
    }
}
