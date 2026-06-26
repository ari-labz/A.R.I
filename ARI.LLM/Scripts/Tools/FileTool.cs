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

    /// <summary>True if the text is one of ARI's history-redaction placeholders. History compaction
    /// hides earlier write/edit payloads behind these; a weak model can copy the placeholder back as
    /// real content and erase a file, so write/edit must refuse it.</summary>
    protected static bool IsRedactionPlaceholder(string? s)
    {
        string t = (s ?? "").Trim();
        return t is "[content omitted]" or "[omitted]";
    }
}
