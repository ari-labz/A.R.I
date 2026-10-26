namespace ARI.API;

/// <summary>
/// Decides whether a thread bound to a project runs on the Code pipeline. Deterministic, so she never has to
/// notice "we're coding now" herself: a project is a workspace (a code repo or a note vault), and anything
/// that isn't a note vault is coding.
/// </summary>
public static class ProjectCodeRules
{
    // Category is descriptive free text, but a project the user describes as a note collection is not code.
    private static readonly string[] NoteCategoryWords = ["vault", "notes", "obsidian", "journal", "wiki"];

    public static bool RunsAsCode(Project project)
    {
        // Files on this server: look at them. An Obsidian vault is notes; any other folder is code.
        if (project.RootPath is { } root && Directory.Exists(root))
            return !Directory.Exists(Path.Combine(root, ".obsidian"));

        // Files on the user's own machine (desktop-attached): nothing here to inspect, so go by how the
        // project is described. The desktop app is a coding client — the default is code.
        return !NoteCategoryWords.Any(w => project.Category.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}
