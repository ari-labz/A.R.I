using ARI.BrainVault;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>Works out where Recall starts in the brain for a message: who is speaking, who is listening, and the names of notes that might matter
/// (names only, never the notes themselves).</summary>
internal sealed class MemoryApi(Memory memory, Thread thread, string? contextSummary, PrivacyMode privacy)
{
    public StartingPoints Start(string message)
    {
        string audience = thread.IsGuarded
            ? "GUARDED. The person talking is not the Master. Share what is safe and relevant; leave out anything private or sensitive."
            : "MASTER. The person talking is the Master, so everything relevant can be shared.";
        // The same gate recall has always had: nothing for a conversation without memory access.
        if (memory.HopLimit <= 0 || !thread.HasMemoryAccess) return new StartingPoints(null, audience);
        Memory.MemoryPrep prep = memory.Prepare(thread.GetChatHistory(), message, contextSummary, privacy);
        // Names only, never the notes themselves: the walker has to open a note with its tools before it can use anything in it.
        StringBuilder text = new();
        if (prep.SpeakerNoteName is { Length: > 0 }) text.AppendLine("THE SPEAKER'S OWN NOTE: " + prep.SpeakerNoteName).AppendLine();
        if (Brain.GetNote("ARI") is { } self) text.AppendLine("ARI'S OWN NOTE (who she is; open it first): " + self.Title).AppendLine();
        if (prep.Terms.Count > 0) text.AppendLine("WORDS FROM THE MESSAGE THAT WERE SEARCHED FOR: " + string.Join(", ", prep.Terms)).AppendLine();
        text.AppendLine(prep.Offered.Count > 0
            ? "NOTES THAT MIGHT BE RELEVANT, BEST MATCH FIRST (names only: open a note with recall_memory to read it):\n" + string.Join("\n", prep.Offered.Select(n => "- " + n))
            : "No note matched those words directly. Search for the people and things the conversation is about.");
        // A capitalised name in the message that no note matches: ARI doesn't know who or what it is, so she should ask, not guess.
        string speaker = thread.History.OfType<Prompt>().Select(p => p.AuthorName).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
        List<string> unknown = System.Text.RegularExpressions.Regex.Matches(message, @"(?<![.!?]\s)(?<!^)\b[A-Z][a-zA-Z'’-]{2,}\b").Select(m => m.Value).Distinct()
            .Where(w => !w.Equals(speaker, StringComparison.OrdinalIgnoreCase) && !w.Equals("ARI", StringComparison.OrdinalIgnoreCase) && !w.Equals("Ari", StringComparison.OrdinalIgnoreCase))
            .Where(w => !prep.Offered.Any(n => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(w => Brain.GetNote(w) is null).ToList();
        if (unknown.Count > 0) text.AppendLine().AppendLine("NAMES IN THE MESSAGE WITH NO NOTE AT ALL: " + string.Join(", ", unknown) + ". Nothing is known about them yet.");
        return new StartingPoints(text.ToString().TrimEnd(), audience);
    }

}

/// <summary><see cref="Text"/> is null when this conversation has no memory access, which tells a step that depends on it to skip itself.</summary>
internal sealed record StartingPoints(string? Text, string Audience);
