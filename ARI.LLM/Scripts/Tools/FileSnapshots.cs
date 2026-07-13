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

    // Ranges already read per file. Re-reading a range the model already has in context re-injects
    // thousands of UNCACHED tokens (a big re-read measured ~150s of prompt processing) and adds nothing —
    // so a covered OR heavily-overlapping re-read is short-circuited with a pointer. This is the read-dedup
    // the remote path had and the local (server-disk) path lacked; the redundancy algorithm itself is the
    // shared static <see cref="RedundancyNudge"/> so BOTH filesystems decide identically (parity).
    private readonly Dictionary<string, List<(int Start, int End)>> readRanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A nudge if this read is already (mostly) in context from an earlier read this session, else null.</summary>
    internal string? RedundantRead(string absPath, int reqStart, int reqEnd)
        => readRanges.TryGetValue(absPath, out List<(int, int)>? r) ? RedundancyNudge(absPath, r, reqStart, reqEnd) : null;

    /// <summary>Record a range that was actually returned to the model, for later dedup.</summary>
    internal void RecordRead(string absPath, int start, int end)
    {
        if (!readRanges.TryGetValue(absPath, out List<(int, int)>? r)) readRanges[absPath] = r = new();
        r.Add((start, end));
    }

    /// <summary>File changed on disk — its recorded read ranges are stale (line numbers shifted).</summary>
    internal void InvalidateReads(string absPath) => readRanges.Remove(absPath);

    /// <summary>
    /// Shared redundancy policy for read_file, used by BOTH the server-disk and client-disk backends so the
    /// two behave identically. Fires when the requested range is fully covered by, or overlaps ≥60% of, a
    /// single range already read this session. Clean consecutive windows (1-100 then 101-200) do not overlap
    /// and pass through; only re-reads and nudged windows of an already-read span are caught.
    /// </summary>
    internal static string? RedundancyNudge(string path, IReadOnlyList<(int Start, int End)> prior, int reqStart, int reqEnd)
    {
        foreach ((int s, int e) in prior)
            if (s <= reqStart && e >= reqEnd)
            {
                string seen = e == int.MaxValue ? $"from line {s} to the end" : $"lines {s}-{e}";
                return $"[Already read] You already read {seen} of '{path}' this session — scroll up to that result rather than re-reading it. Read a genuinely different range only if you must; otherwise you have this file, move on.";
            }

        if (reqEnd != int.MaxValue && reqEnd >= reqStart)
        {
            int reqLen = reqEnd - reqStart + 1;
            foreach ((int s, int e) in prior)
            {
                if (e == int.MaxValue) continue;
                int overlap = Math.Min(e, reqEnd) - Math.Max(s, reqStart) + 1;
                if (overlap > 0 && overlap >= 0.6 * reqLen)
                    return $"[Already read] Lines {reqStart}-{reqEnd} of '{path}' mostly overlap lines {s}-{e} you already read this session — scroll up rather than re-reading a nudged window. You have enough of this file; move on and plan.";
            }
        }
        return null;
    }

    /// <summary>Saves <paramref name="content"/> as the pre-edit snapshot for <paramref name="absPath"/>.
    /// Always overwrites so revert_file always targets the most recent edit.</summary>
    internal void TakeSnapshot(string absPath, string content)
        => snapshots[absPath] = content;

    /// <summary>Restores the snapshot for <paramref name="absPath"/>.
    /// Returns false when no snapshot exists (file was never edited this session).</summary>
    internal bool TryRestore(string absPath, out string content)
        => snapshots.TryGetValue(absPath, out content!);
}
