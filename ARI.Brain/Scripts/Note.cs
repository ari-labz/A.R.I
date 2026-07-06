using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ARI.Brain;

/// <summary>
/// A note in Ari's brain. One markdown file is the source of truth; one row in the index points at it.
/// Instances are lightweight handles — content is read from disk on demand, never held resident.
/// </summary>
public class Note
{
    private const string TIMESTAMP_FORMAT = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly Regex frontmatterBlock = new(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex aliasesLine = new(@"^aliases: \[(.*)\]$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex createdLine = new(@"^created: (\S+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex quotedValue = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    internal readonly long id;

    public string Title { get; }

    /// <summary>Vault-relative file path, e.g. "People/[REDACT].md".</summary>
    public string Path { get; }

    public IReadOnlyList<string> Aliases { get; }

    internal Note(long id, string title, string path, IReadOnlyList<string> aliases)
    {
        this.id = id;
        Title = title;
        Path = path;
        Aliases = aliases;
    }

    /// <summary>Full note name in "Folder/Sub/Title" form.</summary>
    public string Name => Path[..^".md".Length];

    public string Folder => Name.Contains('/') ? Name[..Name.LastIndexOf('/')] : string.Empty;

    /// <summary>Markdown body, read fresh from the file (frontmatter stripped).</summary>
    public string Content => Parse(File.ReadAllText(AbsolutePath)).Body;

    /// <summary>Deep link that opens this note in Obsidian.</summary>
    public string Url => $"obsidian://open?vault={Uri.EscapeDataString(BrainModule.VaultName)}&file={Uri.EscapeDataString(Name)}";

    /// <summary>Notes this note links to.</summary>
    public List<Note> GetLinks() => Database.QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE noteID IN (SELECT noteIDTo FROM connections WHERE noteIDFrom = $id)", ("$id", id));

    /// <summary>Notes that link to this note.</summary>
    public List<Note> GetReferences() => Database.QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE noteID IN (SELECT noteIDFrom FROM connections WHERE noteIDTo = $id)", ("$id", id));

    /// <summary>True when a folder of child notes exists beside this note (it is a hub).</summary>
    public bool HasChildren() => Directory.Exists(System.IO.Path.Combine(BrainModule.VaultRoot, Name))
        && Directory.EnumerateFiles(System.IO.Path.Combine(BrainModule.VaultRoot, Name), "*.md", SearchOption.AllDirectories).Any();

    /// <summary>Direct child notes of this hub.</summary>
    public List<Note> GetChildren() => Database.QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE path LIKE $inside AND path NOT LIKE $deeper",
        ("$inside", $"{Name}/%"), ("$deeper", $"{Name}/%/%"));

    /// <summary>Replaces the note's content and aliases on disk, then refreshes the index.</summary>
    public void Save(string content, IReadOnlyList<string> aliases)
    {
        Write(Path, content, aliases, Parse(File.ReadAllText(AbsolutePath)).Created);
        BrainModule.Index();
    }

    /// <summary>Deletes the note file and its index rows. Hubs with children must be dissolved first.</summary>
    public void Delete()
    {
        if (HasChildren()) throw new InvalidOperationException($"'{Title}' has child notes and cannot be deleted.");
        File.Delete(AbsolutePath);
        BrainModule.Index();
    }

    /// <summary>Folds this note into another: aliases transfer, content is appended nothing — the winner's text stands.
    /// Existing [[links]] to this title keep resolving because the title becomes an alias of the winner.</summary>
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

    /// <summary>The shape agents see in prompts.</summary>
    public string ToPrompt() => $"Path: {Name}\n\n{Content}";

    private string AbsolutePath => System.IO.Path.Combine(BrainModule.VaultRoot, Path);

    // ── File format (owned by Note: parsing and composing the frontmatter) ─────────────

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

    /// <summary>Atomic write (temp + rename): a crash can never leave a half-written note.</summary>
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
