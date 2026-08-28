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
}
