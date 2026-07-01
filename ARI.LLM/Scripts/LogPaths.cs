namespace ARI.LLM;

/// <summary>
/// Resolves on-disk locations for debug artefacts that live at the repo root (run logs,
/// chat-history transcripts). Walks up from the running binary to the repo root
/// (the directory holding ARI.sln); falls back to ~/.ari/Server for a published build that
/// has been detached from the source tree.
/// </summary>
internal static class LogPaths
{
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ARI.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server");
    }

    /// <summary>Returns (creating if needed) the named sub-folder directly under the repo root.</summary>
    internal static string Dir(string sub)
    {
        string path = Path.Combine(RepoRoot.Value, sub);
        Directory.CreateDirectory(path);
        return path;
    }
}
