namespace ARI.LLM;

/// <summary>
/// Stores the file content captured just before each edit_file write, so revert_file can undo the
/// last edit on a per-file basis. One instance per Code thread session (created in CodePipeline and
/// shared between EditFile and RevertFile for that session).
/// </summary>
internal sealed class FileSnapshots
{
    private readonly Dictionary<string, string> snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves <paramref name="content"/> as the pre-edit snapshot for <paramref name="absPath"/>.
    /// Always overwrites so revert_file always targets the most recent edit.</summary>
    internal void TakeSnapshot(string absPath, string content)
        => snapshots[absPath] = content;

    /// <summary>Restores the snapshot for <paramref name="absPath"/>.
    /// Returns false when no snapshot exists (file was never edited this session).</summary>
    internal bool TryRestore(string absPath, out string content)
        => snapshots.TryGetValue(absPath, out content!);
}
