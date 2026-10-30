namespace ARI.Common;

/// <summary>
/// Anything the owner wants to add by hand to what Ari is told about herself, in AppDataRoot/Server/Capabilities.md. What she can actually do
/// is no longer written here: it is generated each conversation from the tools that are really available (CapabilitiesBlock in ARI.LLM),
/// so it can't go stale or promise something that is off. The old default list (and the copy older installs saved from it) is ignored.
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
                if (!File.Exists(FilePath)) return "";
                string text = File.ReadAllText(FilePath);
                return IsLegacyDefault(text) ? "" : text;
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

    // The hand-written list this file used to start with. Older installs saved a copy, with master or owner depending on when, so it is matched
    // on its opening line and its "never say you can't" line rather than byte for byte; anything the owner changed beyond that still counts.
    private static bool IsLegacyDefault(string text)
    {
        string t = text.Trim();
        return t.StartsWith("You are capable of, once the relevant tool is loaded:", StringComparison.Ordinal)
            && t.Contains("Never tell the", StringComparison.Ordinal) && t.Contains("you can. If a tool isn't loaded yet", StringComparison.Ordinal)
            && t.Split('\n').Count(l => l.StartsWith("- ")) <= 9;
    }
}
