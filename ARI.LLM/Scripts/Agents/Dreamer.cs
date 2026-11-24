namespace ARI.LLM;

/// <summary>
/// Drives the dream pipeline. Runs freely with read-only tools and Wake. No Engram, no memory prefill, no user.
/// A dream is one long run through subjects: when she finishes one (stops calling tools) without waking anyone, she is
/// asked to pick a different subject. It ends when she calls Wake, when she finishes a subject and then has nothing new to
/// start, or when a real user request interrupts it. History is compacted into her own summary as it fills the context.
/// </summary>
internal sealed class Dreamer : Agent
{
    internal ToolResult? WakeRequest { get; private set; }

    internal override bool UseSystemContinuation => true;

    internal override int SummaryCompactPct => 60;
    internal override string SummaryCompactInstruction =>
        "You're compacting your own dream so it can carry on. Below is everything you've done in it so far. Write yourself a summary to " +
        "continue from: each subject you've covered, what you concluded about it and whether it was worth waking your master; the notes, " +
        "chats and files you've already read and the facts in them that matter, so you don't read them again; and what you were in the " +
        "middle of and meant to do next. Plain notes, first person, no preamble.";

    /// <summary>Called with each tool's name as she uses it, for the dream's summary log.</summary>
    internal Action<string>? ToolCalled { get; set; }

    // Subjects finished this quiet stretch (kept across dreams until someone talks to her), and tool calls this dream.
    private List<string> covered = new();
    private int toolCalls, toolCallsAtLastFinish = -1;

    internal IReadOnlyList<string> CoveredSubjects => covered;
    internal void ForgetSubjects() => covered = new();
    internal void BeginDream() { toolCalls = 0; toolCallsAtLastFinish = -1; }

    internal void NoteToolCall(string name)
    {
        toolCalls++;
        ToolCalled?.Invoke(name);
    }

    // A step with no tool call is her finishing a subject. Unless she woke someone, she moves on to a different one; finishing twice
    // with nothing looked at in between means she has nothing left, and the dream ends.
    internal override string? OnStepComplete(Thread thread, string stepText, bool hadTools)
    {
        if (!thread.Key.StartsWith("dream-", StringComparison.Ordinal)) return null;   // a compaction summary, not the dream itself
        if (WakeRequest is not null || Sleep.Requested is not null || toolCalls == toolCallsAtLastFinish) return null;
        toolCallsAtLastFinish = toolCalls;
        covered.Add(SubjectOf(stepText));
        return "That subject is finished. Subjects already covered: " + string.Join("; ", covered) + ".\n" +
               "Pick a different subject, not one of those, and start on it. If there's truly nothing else you want to look at, write one closing line and the dream ends.";
    }

    private static string SubjectOf(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string l = line.Trim();
            if (l.StartsWith("topic:", StringComparison.OrdinalIgnoreCase)) return Short(l[6..].Trim());
        }
        string first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length > 0 ? Short(first) : "(unnamed subject)";

        static string Short(string s) => s.Length <= 80 ? s : s[..80] + "…";
    }

    internal void RecordWake(ToolResult result) => WakeRequest = result;
    internal void ResetWake()                   => WakeRequest = null;
}
