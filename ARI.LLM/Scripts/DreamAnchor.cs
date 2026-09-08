namespace ARI.LLM;

/// <summary>
/// Produces the starting instruction injected into the dream's system prompt each turn.
/// Alternates between directing ARI to explore her projects or search her brain, so she
/// covers different territory across dream sessions rather than repeating the same entry point.
/// </summary>
internal static class DreamAnchor
{
    private static readonly Random Rng = new();

    private static readonly string[] StartingPoints =
    [
        "Begin by calling list_projects to see what projects you have. Pick one that interests you, use bind_project to load it, then use list_files to see what's in it and read_file to open files that catch your attention. The project is local — do not search the web for it.",
        "Begin by calling search_brain with a topic that has been on your mind lately. Follow the connections you find.",
        "Begin by calling list_projects. Choose a project you haven't thought about in a while, bind_project to load it, then use list_files and read_file to explore its contents. Keep it local — these are personal projects, not public ones.",
        "Begin by calling search_brain. Look for something unresolved — a question, a gap, something you want to understand better.",
    ];

    internal static string Pull() => StartingPoints[Rng.Next(StartingPoints.Length)];

    /// <summary>
    /// Pull() plus grounding context every dream turn should start from: the actual current date/time
    /// (so nothing has to guess or invent one — see the Sept 2026 incident where a dream fabricated a
    /// conflicting date header that was never in its prompt) and a digest of what recent dreams have
    /// already woken the owner about (so a fresh dream doesn't independently re-raise the same thing).
    /// </summary>
    internal static string PullWithGrounding()
    {
        string time   = $"Right now it's {DateTime.Now:dddd, d MMMM yyyy — HH:mm}.";
        string digest = WakeHistory.Digest();

        return string.IsNullOrEmpty(digest)
            ? $"{time}\n\n{Pull()}"
            : $"{time}\n\n{digest}\n\n{Pull()}";
    }
}
