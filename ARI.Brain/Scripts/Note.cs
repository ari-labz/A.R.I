using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ARI.Brain;

public class Note
{
    private const string TIMESTAMP_FORMAT = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly Regex frontmatterBlock = new(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex aliasesLine = new(@"^aliases: \[(.*)\]$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex createdLine = new(@"^created: (\S+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex quotedValue = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    internal readonly long id;

    public string Title { get; }
    public string Path { get; }
    public IReadOnlyList<string> Aliases { get; }

    internal Note(long id, string title, string path, IReadOnlyList<string> aliases)
    {
        this.id = id;
        Title = title;
        Path = path;
        Aliases = aliases;
    }

    public string Name => Path[..^".md".Length];
    public string Folder => Name.Contains('/') ? Name[..Name.LastIndexOf('/')] : string.Empty;

    // Read fresh from disk on every access — never held resident.
    public string Content => Parse(File.ReadAllText(AbsolutePath)).Body;

    public string Url => $"obsidian://open?vault={Uri.EscapeDataString(BrainModule.VaultName)}&file={Uri.EscapeDataString(Name)}";

    public List<Note> GetLinks() => Database.LinksFrom(id);

    public List<Note> GetReferences() => Database.LinksTo(id);

    public bool HasChildren() => Directory.Exists(System.IO.Path.Combine(BrainModule.VaultRoot, Name))
        && Directory.EnumerateFiles(System.IO.Path.Combine(BrainModule.VaultRoot, Name), "*.md", SearchOption.AllDirectories).Any();

    public List<Note> GetChildren() => Database.ChildrenOf(Name);

    public void Save(string content, IReadOnlyList<string> aliases)
    {
        Write(Path, content, aliases, Parse(File.ReadAllText(AbsolutePath)).Created);
        BrainModule.Index();
    }

    public void Delete()
    {
        if (HasChildren()) throw new InvalidOperationException($"'{Title}' has child notes and cannot be deleted.");
        File.Delete(AbsolutePath);
        BrainModule.Index();
    }

    // The old title becomes an alias on the winner, so existing [[links]] keep resolving.
    public void MergeInto(Note winner)
    {
        if (HasChildren()) throw new InvalidOperationException($"'{Title}' is a hub and cannot be merged away.");
        List<string> combined = new(winner.Aliases);
        foreach (string alias in Aliases.Append(Title))
            if (!combined.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(alias, winner.Title, StringComparison.OrdinalIgnoreCase))
                combined.Add(alias);
        string content = winner.Content;
        File.Delete(AbsolutePath);
        Write(winner.Path, content, combined, null);
        BrainModule.Index();
    }

    public string ToPrompt() => $"Path: {Name}\n\n{Content}";

    private string AbsolutePath => System.IO.Path.Combine(BrainModule.VaultRoot, Path);

    // ── File format ──────────────────────────────────────────────────────────────────

    internal record Parsed(string Body, IReadOnlyList<string> AliasList, DateTime? Created);

    internal static Parsed Parse(string raw)
    {
        string body = raw;
        List<string> aliases = new();
        DateTime? created = null;

        Match frontmatter = frontmatterBlock.Match(raw);
        if (frontmatter.Success)
        {
            body = raw[frontmatter.Length..];
            string head = frontmatter.Groups[1].Value;
            Match aliasMatch = aliasesLine.Match(head);
            if (aliasMatch.Success)
                foreach (Match value in quotedValue.Matches(aliasMatch.Groups[1].Value))
                    aliases.Add(value.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            Match createdMatch = createdLine.Match(head);
            if (createdMatch.Success && DateTime.TryParse(createdMatch.Groups[1].Value, null, DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                created = parsed;
        }
        return new Parsed(body, aliases, created);
    }

    // temp + rename: a crash can never leave a half-written note.
    internal static void Write(string relativePath, string body, IReadOnlyList<string> aliases, DateTime? created)
    {
        string file = System.IO.Path.Combine(BrainModule.VaultRoot, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

        StringBuilder content = new();
        content.AppendLine("---");
        if (aliases.Count > 0)
            content.AppendLine("aliases: [" + string.Join(", ",
                aliases.Select(alias => $"\"{alias.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"")) + "]");
        content.AppendLine($"created: {(created ?? DateTime.UtcNow).ToString(TIMESTAMP_FORMAT)}");
        content.AppendLine($"updated: {DateTime.UtcNow.ToString(TIMESTAMP_FORMAT)}");
        content.AppendLine("---");
        content.Append(body.TrimEnd());
        content.AppendLine();

        string temp = $"{file}.tmp";
        File.WriteAllText(temp, content.ToString());
        File.Move(temp, file, overwrite: true);
    }
}
