namespace ARI.LLM;

/// <summary>
/// Stores the file content captured just before each edit_file write, so revert_file can undo the
/// last edit on a per-file basis. One instance per Code thread session (created in CodePipeline and
/// shared between EditFile and RevertFile for that session).
/// </summary>
internal sealed class FileSnapshots
{
    private readonly Dictionary<string, string> snapshots = new(StringComparer.OrdinalIgnoreCase);

    // preview_file must precede read_file (keeps context lean — the model sees the line count + a
    // large-file warning before committing to a ranged read). Tracks which files were previewed this
    // session so read_file can refuse a blind read. Shared across the architect and its Coders.
    private readonly HashSet<string> previewed = new(StringComparer.OrdinalIgnoreCase);
    internal void MarkPreviewed(string absPath) => previewed.Add(absPath);
    internal bool WasPreviewed(string absPath)  => previewed.Contains(absPath);

    /// <summary>Saves <paramref name="content"/> as the pre-edit snapshot for <paramref name="absPath"/>.
    /// Always overwrites so revert_file always targets the most recent edit.</summary>
    internal void TakeSnapshot(string absPath, string content)
        => snapshots[absPath] = content;

    /// <summary>Restores the snapshot for <paramref name="absPath"/>.
    /// Returns false when no snapshot exists (file was never edited this session).</summary>
    internal bool TryRestore(string absPath, out string content)
        => snapshots.TryGetValue(absPath, out content!);
}
