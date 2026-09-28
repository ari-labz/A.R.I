using System.Text;

namespace ARI.LLM;

/// <summary>
/// A short map of a project folder — its top two levels, build/dependency folders skipped — put in a
/// project thread's context at the start so ARI knows what's there without being asked to look.
/// </summary>
internal static class ProjectOverview
{
    private const int MAX_ENTRIES = 80;
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".idea", ".vscode", ".ariproject", "__pycache__", ".DS_Store" };

    internal static string Build(string root)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Project root: {root}");
        sb.AppendLine("Top two levels (build and dependency folders skipped — use list_directory to see more):");
        int count = 0;
        bool truncated = false;
        Walk(root, 1, "", sb, ref count, ref truncated);
        if (truncated) sb.AppendLine($"… (stopped at {MAX_ENTRIES} entries)");
        return sb.ToString().TrimEnd();
    }

    private static void Walk(string dir, int depth, string indent, StringBuilder sb, ref int count, ref bool truncated)
    {
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

        // Folders first, then files — the shape of a project reads from its folders.
        foreach (string entry in entries.Where(Directory.Exists).Concat(entries.Where(File.Exists)))
        {
            string name = Path.GetFileName(entry);
            if (Skipped.Contains(name)) continue;
            if (count >= MAX_ENTRIES) { truncated = true; return; }
            count++;
            bool isDir = Directory.Exists(entry);
            sb.AppendLine($"{indent}{name}{(isDir ? "/" : "")}");
            if (isDir && depth < 2) Walk(entry, depth + 1, indent + "  ", sb, ref count, ref truncated);
        }
    }
}
