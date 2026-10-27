using System.Text;
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
        // The same gate the Memory step has always had: no recall for a conversation that has no memory access.
        if (memory.HopLimit <= 0 || !thread.HasMemoryAccess) return new MemoryFound(JsonSerializer.Serialize(new Memory.MemoryPrep(true, "", "", "", "", [], null, [], 0, 0, "", message, null, Disabled: true)), null, null);
        Memory.MemoryPrep prep = memory.Prepare(thread.GetChatHistory(), message, contextSummary, privacy);
        return new MemoryFound(JsonSerializer.Serialize(prep), prep.Done ? null : prep.Transcript, prep.Done ? null : prep.CandidateText);
    }

    public StartingPoints Start(string message)
    {
        string audience = thread.IsGuarded
            ? "GUARDED. The person talking is not the Master. Share what is safe and relevant; leave out anything private or sensitive."
            : "MASTER. The person talking is the Master, so everything relevant can be shared.";
        // The same gate recall has always had: nothing for a conversation without memory access.
        if (memory.HopLimit <= 0 || !thread.HasMemoryAccess) return new StartingPoints(null, audience);
        Memory.MemoryPrep prep = memory.Prepare(thread.GetChatHistory(), message, contextSummary, privacy);
        StringBuilder text = new();
        if (prep.Pinned.Length > 0) text.AppendLine("THE SPEAKER'S OWN NOTE:").AppendLine(prep.Pinned.TrimEnd()).AppendLine();
        if (prep.Terms.Count > 0) text.AppendLine("WORDS FROM THE MESSAGE THAT WERE SEARCHED FOR: " + string.Join(", ", prep.Terms)).AppendLine();
        text.AppendLine(prep.CandidateText.Length > 0
            ? "NOTES THAT MATCH, BEST FIRST (read the ones that matter; follow their links):\n" + prep.CandidateText.TrimEnd()
            : "No note matched those words directly. Search for the people and things the conversation is about.");
        return new StartingPoints(text.ToString().TrimEnd(), audience);
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
