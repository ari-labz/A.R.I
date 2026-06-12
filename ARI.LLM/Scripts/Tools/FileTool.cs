namespace ARI.LLM;

/// <summary>Shared base for tools that operate on files within a project root.</summary>
internal abstract class FileTool : Tool
{
    protected readonly string            root;
    protected readonly CancellationToken ct;

    protected FileTool(string root, CancellationToken ct)
    {
        this.root = root;
        this.ct   = ct;
    }

    /// <summary>Resolves a project-relative path to an absolute one, or null if it escapes the root.</summary>
    protected string? Resolve(string relPath)
    {
        string absPath = Path.GetFullPath(Path.Combine(root, relPath));
        return absPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? absPath : null;
    }
}
