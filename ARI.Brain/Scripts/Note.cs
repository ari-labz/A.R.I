using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ARI.Brain;

public class Note
{
    private const string TIMESTAMP_FORMAT = "yyyy-MM-ddTHH:mm:ssZ";

    private static readonly Regex frontmatterBlock = new(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex aliasesLine = new(@"^aliases: \[(.*)\]$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex typeLine = new(@"^type: (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex createdLine = new(@"^created: (\S+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex quotedValue = new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static readonly Regex thoughtHeader = new(@"^\s*>\s*\[!ari-thought\]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex thoughtMeta   = new(@"^\s*>\s*confidence:\s*([^·]+)·\s*([^·]+)·\s*kind:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex listMarker    = new(@"^[-*]\s+(.*)$", RegexOptions.Compiled);
    private const string ORPHAN_HEADER = "## Ari's Thoughts (needs re-anchoring)";

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

    // Node type from frontmatter (null = leaf). Read fresh, like Content.
    public string? Type => Parse(File.ReadAllText(AbsolutePath)).Type;

    public string Url => $"obsidian://open?vault={Uri.EscapeDataString(BrainModule.VaultName)}&file={Uri.EscapeDataString(Name)}";

    public List<Note> GetLinks() => Database.LinksFrom(id);

    public List<Note> GetReferences() => Database.LinksTo(id);

    public bool HasChildren() => Directory.Exists(System.IO.Path.Combine(BrainModule.VaultRoot, Name))
        && Directory.EnumerateFiles(System.IO.Path.Combine(BrainModule.VaultRoot, Name), "*.md", SearchOption.AllDirectories).Any();

    public List<Note> GetChildren() => Database.ChildrenOf(Name);

    public void Save(string content, IReadOnlyList<string> aliases)
    {
        Write(Path, CarryThoughtsInto(Content, content), aliases, Parse(File.ReadAllText(AbsolutePath)).Created);
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
        string loserTitle = Title;
        string content = CarryThoughtsInto(Content, winner.Content);
        File.Delete(AbsolutePath);
        Write(winner.Path, content, combined, null);
        // Repoint every [[loserTitle]] to the winner so referrers don't keep pointing at the folded-away
        // note. The loser title is also kept as an alias on the winner, so anything missed still resolves.
        BrainModule.RepointReferences(loserTitle, winner.Title);
        BrainModule.Index();
    }

    public string ToPrompt() => Aliases.Count > 0
        ? $"Path: {Name}\nAliases: {string.Join(", ", Aliases)}\n\n{Content}"
        : $"Path: {Name}\n\n{Content}";

    // spanText must be a verbatim substring of this note's current content — anchors the callout
    // directly under the line/bullet it's about. Falls back to an orphaned, re-anchorable entry
    // if the text can't be found (e.g. the note changed between when the thought was formed and written).
    public void AddThought(string spanText, string comment, string confidence, string kind)
    {
        ParsedThought thought = new(kind, spanText, comment, confidence, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        string newBody = InsertThought(Content, thought);
        Write(Path, newBody, Aliases, Parse(File.ReadAllText(AbsolutePath)).Created);
        BrainModule.Index();
    }

    private string AbsolutePath => System.IO.Path.Combine(BrainModule.VaultRoot, Path);

    // ── Thoughts (margin annotations) ────────────────────────────────────────────────

    internal record ParsedThought(string Kind, string SpanText, string Comment, string Confidence, string Created);

    // Scans a note body for `> [!ari-thought]` callouts and reads each one's anchor (the nearest
    // preceding non-blockquote, non-blank line). Used both by Database.Rebuild (indexing) and by
    // the strip/reinsert safety net around edits and merges.
    internal static List<ParsedThought> ParseThoughts(string body)
    {
        List<ParsedThought> thoughts = new();
        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match header = thoughtHeader.Match(lines[i]);
            if (!header.Success) continue;

            string comment    = header.Groups[1].Value.Trim();
            string confidence = "unknown";
            string created    = string.Empty;
            string kind        = "observation";
            if (i + 1 < lines.Length)
            {
                Match meta = thoughtMeta.Match(lines[i + 1]);
                if (meta.Success)
                {
                    confidence = meta.Groups[1].Value.Trim();
                    created    = meta.Groups[2].Value.Trim();
                    kind       = meta.Groups[3].Value.Trim();
                }
            }

            string spanText = string.Empty;
            for (int j = i - 1; j >= 0; j--)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith('>')) continue;
                Match marker = listMarker.Match(candidate);
                spanText = marker.Success ? marker.Groups[1].Value : candidate;
                break;
            }

            thoughts.Add(new ParsedThought(kind, spanText, comment, confidence, created));
        }
        return thoughts;
    }

    // Removes every `> [!ari-thought]` callout (header + meta line) from a body, returning the
    // clean body and what was removed — used before an LLM-driven edit so the model never sees
    // (and can't garble) previously-recorded thoughts.
    internal static (string Body, List<ParsedThought> Removed) StripThoughts(string body)
    {
        List<ParsedThought> removed = ParseThoughts(body);
        if (removed.Count == 0) return (body, removed);

        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        List<string> kept = new();
        for (int i = 0; i < lines.Length; i++)
        {
            if (thoughtHeader.IsMatch(lines[i]))
            {
                if (i + 1 < lines.Length && thoughtMeta.IsMatch(lines[i + 1])) i++;
                continue;
            }
            kept.Add(lines[i]);
        }
        string stripped = Regex.Replace(string.Join('\n', kept), @"\n{3,}", "\n\n").TrimEnd() + "\n";
        return (stripped, removed);
    }

    // Inserts one thought under its anchor if the anchor text still exists in the body, otherwise
    // appends it to a trailing "needs re-anchoring" section so nothing is silently lost.
    internal static string InsertThought(string body, ParsedThought thought)
    {
        const string calloutIndent = "    ";
        string callout = $"{calloutIndent}> [!ari-thought] {thought.Comment}\n" +
                          $"{calloutIndent}> confidence: {thought.Confidence} · {thought.Created} · kind: {thought.Kind}";

        int anchorIndex = string.IsNullOrEmpty(thought.SpanText) ? -1 : body.IndexOf(thought.SpanText, StringComparison.Ordinal);
        if (anchorIndex >= 0)
        {
            int lineEnd = body.IndexOf('\n', anchorIndex);
            if (lineEnd < 0) lineEnd = body.Length;
            return body[..lineEnd] + "\n" + callout + body[lineEnd..];
        }

        string orphanEntry = $"- Was anchored to: \"{thought.SpanText}\"\n{callout}";
        int headerIndex = body.IndexOf(ORPHAN_HEADER, StringComparison.Ordinal);
        if (headerIndex >= 0)
        {
            int sectionEnd = body.IndexOf("\n## ", headerIndex + ORPHAN_HEADER.Length, StringComparison.Ordinal);
            if (sectionEnd < 0) sectionEnd = body.Length;
            return body[..sectionEnd].TrimEnd() + "\n\n" + orphanEntry + "\n" + body[sectionEnd..];
        }
        return body.TrimEnd() + $"\n\n{ORPHAN_HEADER}\n\n{orphanEntry}\n";
    }

    // Strips thoughts out before an edit is applied, then re-anchors (or orphans) each one against
    // the freshly-written content. Call this around any path that replaces a note's body wholesale.
    internal static string CarryThoughtsInto(string oldBody, string newBody)
    {
        List<ParsedThought> existing = ParseThoughts(oldBody);
        foreach (ParsedThought thought in existing)
            if (!newBody.Contains(thought.Comment, StringComparison.Ordinal)) // skip if already re-emitted by the edit itself
                newBody = InsertThought(newBody, thought);
        return newBody;
    }

    // ── File format ──────────────────────────────────────────────────────────────────

    internal record Parsed(string Body, IReadOnlyList<string> AliasList, DateTime? Created, string? Type);

    internal static Parsed Parse(string raw)
    {
        string body = raw;
        List<string> aliases = new();
        DateTime? created = null;
        string? type = null;

        Match frontmatter = frontmatterBlock.Match(raw);
        if (frontmatter.Success)
        {
            body = raw[frontmatter.Length..];
            string head = frontmatter.Groups[1].Value;
            Match aliasMatch = aliasesLine.Match(head);
            if (aliasMatch.Success)
                foreach (Match value in quotedValue.Matches(aliasMatch.Groups[1].Value))
                    aliases.Add(value.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            Match typeMatch = typeLine.Match(head);
            if (typeMatch.Success) type = typeMatch.Groups[1].Value.Trim();
            Match createdMatch = createdLine.Match(head);
            if (createdMatch.Success && DateTime.TryParse(createdMatch.Groups[1].Value, null, DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                created = parsed;
        }
        return new Parsed(body, aliases, created, type);
    }

    // temp + rename: a crash can never leave a half-written note.
    // type and created are both sticky: when a write doesn't set one, the note's existing value is
    // preserved — so a pass that rewrites a note for other reasons (hub-links, thoughts, merges,
    // renames) never strips its colour group or, worse, silently backdates-forward when it was
    // created. No caller decides this; it's enforced here so it can't be gotten wrong per call site.
    // updated is never sticky — every write is, definitionally, an update.
    internal static void Write(string relativePath, string body, IReadOnlyList<string> aliases, DateTime? created, string? type = null)
    {
        string file = System.IO.Path.Combine(BrainModule.VaultRoot, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

        if (File.Exists(file))
        {
            Parsed existing = Parse(File.ReadAllText(file));
            type    ??= existing.Type;
            created ??= existing.Created;
        }

        StringBuilder content = new();
        content.AppendLine("---");
        if (aliases.Count > 0)
            content.AppendLine("aliases: [" + string.Join(", ",
                aliases.Select(alias => $"\"{alias.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"")) + "]");
        if (!string.IsNullOrWhiteSpace(type))
            content.AppendLine($"type: {type.Trim()}");
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
