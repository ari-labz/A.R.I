namespace ARI.LLM;

/// <summary>memory-experimental only: each Recall change can be switched for A/B runs without a rebuild. ARI_RECALL_EXPERIMENT names a
/// file of "feature=on" / "feature=off" lines (a bare name means off); it's read on every check, so a run can flip them live. The defaults
/// are what the 3 Nov tests found best. Remove this class, keeping the winners and deleting the rest, before merging.</summary>
internal static class RecallExperiment
{
    private static readonly string? switchFile = Environment.GetEnvironmentVariable("ARI_RECALL_EXPERIMENT");

    /// <summary>The 2 Nov task wording ("…ARI can call recall_memory herself") instead of the fixed prompt.</summary>
    internal static bool OldTask        => !Enabled("fixtask", true);
    internal static bool Hint           => Enabled("hint", true);
    internal static bool KeepNotes      => Enabled("keepnotes", true);
    internal static bool Preload        => Enabled("preload", false);
    internal static bool LookAhead      => Enabled("lookahead", false);
    internal static bool Drafts         => Enabled("drafts", false);
    /// <summary>Drafts in a chat that already has messages, not just a new chat's first one.</summary>
    internal static bool ChatDrafts     => Enabled("chatdrafts", false);
    internal static bool Outline        => Enabled("outline", false);
    internal static bool StrictDelta    => Enabled("delta2", true);
    /// <summary>A "Nothing new." turn isn't kept in Recall's thread, and one that read notes for the message is asked once to write them up.</summary>
    internal static bool DropNothingNew => Enabled("dropnn", true);

    private static bool Enabled(string feature, bool byDefault)
    {
        if (switchFile is null) return byDefault;
        try
        {
            if (!File.Exists(switchFile)) return byDefault;
            foreach (string line in File.ReadAllText(switchFile).Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (!parts[0].Equals(feature, StringComparison.OrdinalIgnoreCase)) continue;
                return parts.Length == 2 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
            }
            return byDefault;
        }
        catch (IOException) { return byDefault; }
    }
}
