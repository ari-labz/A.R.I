using System.Text.RegularExpressions;

namespace ARI.Brain;

/// <summary>A note as it exists on disk: the file is the source of truth, this is its parsed view.</summary>
public record VaultNote(string Title, string Path, string Folder, string Content, IReadOnlyList<string> Aliases, DateTime Updated);

public class VaultStore
{
    private static readonly Regex frontmatterBlock = new(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex aliasesLine = new(@"^aliases: \[(.*)\]$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex quotedValue = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex wikiLink = new(@"\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);

    private readonly string root;

    public VaultStore(string vaultPath)
    {
        root = vaultPath;
    }

    /// <summary>Reads every note in the vault. Dot-directories (engine state) are not part of the brain.</summary>
    public List<VaultNote> ScanNotes()
    {
        List<VaultNote> notes = new();
        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            string relative = System.IO.Path.GetRelativePath(root, file);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(segment => segment.StartsWith('.'))) continue;

            string raw = File.ReadAllText(file);
            string body = raw;
            List<string> aliases = new();

            Match frontmatter = frontmatterBlock.Match(raw);
            if (frontmatter.Success)
            {
                body = raw[frontmatter.Length..];
                Match aliasMatch = aliasesLine.Match(frontmatter.Groups[1].Value);
                if (aliasMatch.Success)
                    foreach (Match value in quotedValue.Matches(aliasMatch.Groups[1].Value))
                        aliases.Add(value.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            }

            notes.Add(new VaultNote(
                System.IO.Path.GetFileNameWithoutExtension(file),
                relative,
                System.IO.Path.GetDirectoryName(relative) ?? string.Empty,
                body,
                aliases,
                File.GetLastWriteTimeUtc(file)));
        }
        return notes;
    }

    /// <summary>Extracts [[wikilink]] targets from markdown, deduplicated, in order of first appearance.</summary>
    public static List<string> ParseLinks(string markdown)
    {
        List<string> links = new();
        foreach (Match match in wikiLink.Matches(markdown))
        {
            string target = match.Groups[1].Value.Trim();
            if (target.Length > 0 && !links.Contains(target, StringComparer.OrdinalIgnoreCase)) links.Add(target);
        }
        return links;
    }
}
