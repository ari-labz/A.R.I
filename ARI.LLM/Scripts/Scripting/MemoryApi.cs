using System.Text.Json;
using ARI.Scripting;

namespace ARI.LLM;

/// <summary>Memory's two halves, as a script sees them (see <see cref="IMemoryApi"/>). It's the same code the Memory agent runs
/// on its own; only the model steps differ: here they are the agents the pipeline names.</summary>
internal sealed class MemoryApi(
    Memory memory, Thread thread, string? contextSummary, PrivacyMode privacy,
    Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<string>> ask) : IMemoryApi
{
    public MemoryFound Find(string message)
    {
        if (memory.HopLimit <= 0) return new MemoryFound(JsonSerializer.Serialize(new Memory.MemoryPrep(true, "", "", "", "", [], null, [], 0, 0, "", message, null, Disabled: true)), null, null);
        Memory.MemoryPrep prep = memory.Prepare(thread.GetChatHistory(), message, contextSummary, privacy);
        return new MemoryFound(JsonSerializer.Serialize(prep), prep.Done ? null : prep.Transcript, prep.Done ? null : prep.CandidateText);
    }

    public async Task<string?> Finish(string state, string? picks, CancellationToken ct)
    {
        Memory.MemoryPrep prep = JsonSerializer.Deserialize<Memory.MemoryPrep>(state)!;
        if (prep.Disabled) return null;
        if (prep.Done) return prep.Early;
        Memory.MemoryModelSteps steps = new(
            (request, transcript, noteContent) => ask(SystemAgents.Extract,
                new Dictionary<string, string> { ["request"] = request, ["transcript"] = transcript, ["note_content"] = noteContent }, ct),
            (memories, transcript) => ask(SystemAgents.Privacy,
                new Dictionary<string, string> { ["memories"] = memories, ["transcript"] = transcript }, ct));
        return await memory.Finish(picks ?? "", prep, privacy, steps, ct);
    }
}
