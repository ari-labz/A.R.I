using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Classifier : Agent
{
    public Classifier() { }

    internal async Task<string> Classify(string message, CancellationToken ct = default)
    {
        Thread ephemeral = new Thread(ThreadPipeline.Dialogue, $"__classify_{Guid.NewGuid():N}") { Internal = true };
        string result;
        try
        {
            result = await SendPrompt(ephemeral, message, ct: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A classification failure (e.g. the model never converges within its thinking budget on an
            // ambiguous prompt) must never block the user from getting ANY reply. Dialogue is the safe
            // default — it can still reach code tools/pipelines via list_tools if the request turns out
            // to need them, whereas defaulting to Code would strand a pure-conversation message.
            Shared.Logger.LogWarning(ex, "[Classifier] classification failed — defaulting to Dialogue.");
            return "Dialogue";
        }
        return result.Trim().StartsWith("CODE", StringComparison.OrdinalIgnoreCase) ? "Code" : "Dialogue";
    }
}
