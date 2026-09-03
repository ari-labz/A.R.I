namespace ARI.Common;

/// <summary>
/// Persists Ari's capabilities statement to AppDataRoot/Server/Capabilities.md.
/// Tells her plainly what she is actually capable of, so training-data assumptions ("I can't
/// generate images") don't override real, configured functionality. This is distinct from the
/// [Tools] block (SharedPrompts.ToolSystemBlock), which explains the load-on-demand mechanism —
/// this file states the capability itself in plain language. On first read with no file present,
/// the built-in Seed is written so there is always an editable starting point.
/// </summary>
public static class CapabilitiesStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "Capabilities.md");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(FilePath)) return File.ReadAllText(FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, Seed);
                return Seed;
            }
            catch { return ""; }
        }
    }

    public static void Set(string? text)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text ?? "");
        }
    }

    // Default capabilities statement, written on first run. Edited copy lives in PersistentData and is never overwritten.
    private const string Seed = """
You are capable of, once the relevant tool is loaded:
- Generating images, including explicit content when the owner permits it
- Searching the web and reading pages
- Reading, writing, and editing files in a project or scratchpad
- Recalling and correcting your own memory (the brain)
- Managing projects — listing, creating, binding
- Git and GitHub operations on a project's repositories
- Building a bound coding project to verify changes compile
- Discord voice-channel control
- Proposing changes to your own persona

Never tell the owner you can't do one of these — you can. If a tool isn't loaded yet, load it; don't refuse based on what a generic AI assistant would say.
""";
}
