using System.Text.RegularExpressions;

namespace ARI.BrainVault;

/// <summary>
/// The parts of keeping the brain's shape that need no judgement, so code does them exactly and every time:
/// a name linked from several notes becomes a note of its own instead of a dead link, a plain mention of a note that
/// exists becomes a link, and hub notes list their members. Engram decides where facts belong; this keeps the links honest.
/// </summary>
public static class BrainShape
{
    /// <summary>Notes about one subject should stay small enough to read in one go; past this Engram reworks them.</summary>
    public const int MaxWords = 300;

    private static readonly Regex Link = new(@"\[\[([^\]|#]+?)(#[^\]|]*)?(\|[^\]]*)?\]\]", RegexOptions.Compiled);

    // Folders whose notes are records of time, not subjects: never made into topics, never auto-linked to.
    private static bool IsLog(string path) => path.StartsWith("Conversations/", StringComparison.OrdinalIgnoreCase) || path.Equals("Conversations", StringComparison.OrdinalIgnoreCase);

    private static string NoExt(string p) => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? p[..^3] : p;

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "");

    private static IEnumerable<(string Relative, string File)> VaultFiles() =>
        Directory.EnumerateFiles(Brain.VaultRoot, "*.md", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(Brain.VaultRoot, f).Replace(Path.DirectorySeparatorChar, '/'), f))
            .Where(x => !x.Item1.Split('/').Any(s => s.StartsWith('.')));

    private static (string Head, string Body) SplitFrontmatter(string text)
    {
        Match m = Regex.Match(text, @"^---\n.*?\n---\n?", RegexOptions.Singleline);
        return m.Success ? (text[..m.Length], text[m.Length..]) : ("", text);
    }

    /// <summary>
    /// Links to notes that don't exist are kept, not deleted: they show what the brain is missing.
    /// A misspelt link to a note that does exist (other case, other punctuation) is pointed at it; a name linked from two or more
    /// notes becomes a topic note listing where it came up, for Engram to fill in. Returns (links repointed, topic notes made).
    /// </summary>
    public static (int Repointed, int Created) GrowPendingLinks()
    {
        Dictionary<string, string> byNorm = new();
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (string t in Database.AllTitles()) { known.Add(t); byNorm.TryAdd(Norm(t), t); }
        foreach ((string title, string alias) in Database.AliasPairs()) { known.Add(alias); byNorm.TryAdd(Norm(alias), title); }
        foreach (string p in Database.AllPaths()) { known.Add(p); known.Add(NoExt(p)); }

        int repointed = 0;
        Dictionary<string, HashSet<string>> pending = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string rel, string file) in VaultFiles())
        {
            string text = File.ReadAllText(file);
            string self = Path.GetFileNameWithoutExtension(rel);
            string updated = Link.Replace(text, m =>
            {
                string target = m.Groups[1].Value.Trim();
                if (known.Contains(target)) return m.Value;
                if (byNorm.TryGetValue(Norm(target), out string? real) && !real.Equals(self, StringComparison.OrdinalIgnoreCase))
                {
                    repointed++;
                    string shown = m.Groups[3].Success ? m.Groups[3].Value : (target == real ? "" : "|" + target);
                    return $"[[{real}{m.Groups[2].Value}{shown}]]";
                }
                (pending.TryGetValue(target, out HashSet<string>? from) ? from : pending[target] = new(StringComparer.OrdinalIgnoreCase)).Add(self);
                return m.Value;
            });
            if (updated != text) Replace(file, updated);
        }

        int created = 0;
        foreach ((string target, HashSet<string> from) in pending)
        {
            if (from.Count < 2 || target.Contains('/') || Norm(target).Length < 2) continue;
            if (Brain.GetNote(target) is not null) continue;
            string body = $"# {target}\n\n**Mentioned in:** {string.Join(", ", from.OrderBy(x => x).Select(x => $"[[{x}]]"))}\n";
            Note.Write($"Topics/{Brain.PathFor(target)}", body, Array.Empty<string>(), null, type: "topic");
            created++;
        }
        if (created > 0 || repointed > 0) Brain.Index();
        return (repointed, created);
    }

    // Titles too common to link wherever they appear as a word.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        { "home", "work", "family", "friends", "server", "hub", "notes", "ideas", "projects", "people", "places", "events", "topics", "themes", "relationships", "conversations", "health" };

    /// <summary>A note that names another note in plain words gets the first mention linked, so what the text says connects the
    /// graph. Exact title (or alias) as a whole word only; never a note's own name, never inside a link or heading. Returns notes changed.</summary>
    public static int AutoLink()
    {
        List<(string Needle, string Title)> names = new();
        foreach (string path in Database.AllPaths().Select(NoExt))
        {
            if (IsLog(path) || path.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase)) continue;
            string title = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            if (Usable(title)) names.Add((title, title));
        }
        foreach ((string title, string alias) in Database.AliasPairs()) if (Usable(alias)) names.Add((alias, title));
        names = names.OrderByDescending(n => n.Needle.Length).ToList();   // longest first, so "Mac Studio" wins over "Mac"

        int changed = 0;
        // Day logs link only the few main subjects their entry names; linking every passing mention there pulls everything into one clump.
        foreach ((string rel, string file) in VaultFiles().Where(f => !IsLog(f.Relative)))
        {
            string text = File.ReadAllText(file);
            (string head, string body) = SplitFrontmatter(text);
            string self = Path.GetFileNameWithoutExtension(rel);
            HashSet<string> linked = new(Brain.GetWikilinks(body).Select(l => l.Contains('/') ? l[(l.LastIndexOf('/') + 1)..] : l), StringComparer.OrdinalIgnoreCase);
            string newBody = body;
            foreach ((string needle, string title) in names)
            {
                if (title.Equals(self, StringComparison.OrdinalIgnoreCase) || linked.Contains(title)) continue;
                Match hit = FirstPlainMention(newBody, needle);
                if (!hit.Success) continue;
                string replacement = needle == title ? $"[[{title}]]" : $"[[{title}|{hit.Value}]]";
                newBody = newBody[..hit.Index] + replacement + newBody[(hit.Index + hit.Length)..];
                linked.Add(title);
            }
            if (newBody == body) continue;
            Replace(file, head + newBody);
            changed++;
        }
        if (changed > 0) Brain.Index();
        return changed;

        static bool Usable(string name) => name.Length >= 3 && !Common.Contains(name) && !Regex.IsMatch(name, @"^\d") && (name.Length >= 4 || name.All(c => !char.IsLetter(c) || char.IsUpper(c)));
    }

    // The first whole-word, case-sensitive mention that isn't already inside [[...]], on a heading line, or in a margin thought's metadata.
    private static Match FirstPlainMention(string body, string needle)
    {
        Regex word = new($@"(?<![\w\[|/.@#-]){Regex.Escape(needle)}(?![\w\]/@-])");
        foreach (Match m in word.Matches(body))
        {
            // Never inside a web address, a file path or inline code.
            int tokenStart = m.Index; while (tokenStart > 0 && !char.IsWhiteSpace(body[tokenStart - 1])) tokenStart--;
            string token = body[tokenStart..m.Index];
            if (token.Contains("://") || token.StartsWith("www.") || token.Contains('/') || body[..m.Index].Count(c => c == '`') % 2 == 1) continue;
            int lineStart = body.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            string line = body[lineStart..(body.IndexOf('\n', m.Index) is int e && e >= 0 ? e : body.Length)];
            if (line.TrimStart().StartsWith('#') || line.Contains("confidence:")) continue;
            int open = body.LastIndexOf("[[", m.Index, StringComparison.Ordinal), close = body.LastIndexOf("]]", m.Index, StringComparison.Ordinal);
            if (open > close) continue;   // inside a link
            return m;
        }
        return Match.Empty;
    }

    /// <summary>Writes a hub note listing its members (made or rewritten by code, so it is always exact). Members that don't exist are skipped.</summary>
    public static bool WriteHub(string name, string blurb, IEnumerable<string> members)
    {
        List<string> real = members.Select(m => Brain.GetNote(m)?.Title).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).ToList();
        if (real.Count < 2) return false;
        string title = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        string body = $"# {title}\n\n{blurb.Trim()}\n\n## Members\n" + string.Join("\n", real.Select(m => $"- [[{m}]]")) + "\n";
        Note.Write(Brain.PathFor(name.Contains('/') ? name : $"Themes/{name}"), body, Brain.GetNote(name)?.Aliases ?? Array.Empty<string>(), null, type: "hub");
        Brain.Index();
        return true;
    }

    /// <summary>Notes that have grown past one subject: too long, or (for anything but a hub or log) one block with no headings.</summary>
    public static List<string> Overgrown()
    {
        List<string> list = new();
        foreach (Note n in Database.AllNotes())
        {
            if (IsLog(n.Name) || string.Equals(n.Type, "hub", StringComparison.OrdinalIgnoreCase)) continue;
            int words = n.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > MaxWords) list.Add(n.Name);
        }
        return list;
    }

    private static readonly Regex GroupsLine  = new(@"(?m)^\*\*Groups:\*\*(.*)$", RegexOptions.Compiled);
    private static readonly Regex MembersLine = new(@"(?m)^\*\*Members:\*\*.*$\n?", RegexOptions.Compiled);

    /// <summary>The groups a note body says it belongs to (its "**Groups:**" line).</summary>
    public static List<string> GroupsOf(string body)
    {
        Match m = GroupsLine.Match(body);
        return m.Success ? Brain.GetWikilinks(m.Groups[1].Value).Select(g => g.Contains('/') ? g[(g.LastIndexOf('/') + 1)..] : g).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new();
    }

    /// <summary>Sets a note body's "**Groups:**" line (adding it at the top when missing).</summary>
    public static string WithGroups(string body, IEnumerable<string> groups)
    {
        List<string> gs = groups.Where(g => g.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string line = gs.Count == 0 ? "" : "**Groups:** " + string.Join(", ", gs.Select(g => $"[[{g}]]"));
        if (GroupsLine.IsMatch(body)) return gs.Count == 0 ? GroupsLine.Replace(body, "").TrimStart('\n') : GroupsLine.Replace(body, line, 1);
        return gs.Count == 0 ? body : line + "\n\n" + body.TrimStart();
    }

    /// <summary>
    /// Groups are the brain's neighbourhoods. A note names the groups it belongs to on its "**Groups:**" line, as many as fit; every
    /// group is a note whose "**Members:**" line lists its members, written here so it is always exact. A group named after a note that
    /// exists (a person, a project) is that note; any other gets a hub in Groups/. Groups can belong to groups. Returns groups written.
    /// </summary>
    public static int SyncGroups()
    {
        Brain.Index();
        Dictionary<string, SortedSet<string>> members = new(StringComparer.OrdinalIgnoreCase);
        foreach (Note n in Database.AllNotes())
        {
            if (IsLog(n.Name)) continue;
            foreach (string g in GroupsOf(n.Content))
            {
                if (g.Equals(n.Title, StringComparison.OrdinalIgnoreCase)) continue;
                if (!members.TryGetValue(g, out SortedSet<string>? set)) members[g] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(n.Title);
            }
        }
        int written = 0;
        foreach ((string group, SortedSet<string> ms) in members)
        {
            Note? note = Brain.GetNote(group);
            string line = "**Members:** " + string.Join(", ", ms.Select(m => $"[[{m}]]"));
            if (note is null)
            {
                Note.Write($"Groups/{Brain.PathFor(group)}", line, Array.Empty<string>(), null, type: "group");
                written++;
                continue;
            }
            string body = note.Content.TrimEnd();
            string updated = MembersLine.IsMatch(body + "\n") ? MembersLine.Replace(body + "\n", line + "\n", 1).TrimEnd() : body + "\n\n" + line;
            if (updated == body) continue;
            Note.Write(note.Path, updated, note.Aliases, null);
            written++;
        }
        // A group nobody belongs to any more: its member list goes, and an empty hub goes with it.
        foreach (Note n in Database.AllNotes().ToList())
        {
            if (members.ContainsKey(n.Title) || !MembersLine.IsMatch(n.Content + "\n")) continue;
            string rest = MembersLine.Replace(n.Content + "\n", "").Trim();
            if (n.Name.StartsWith("Groups/", StringComparison.OrdinalIgnoreCase) && GroupsOf(rest).Count == 0 && Regex.Replace(rest, @"\*\*Groups:\*\*.*", "").Trim().Length == 0) n.Delete();
            else Note.Write(n.Path, rest, n.Aliases, null);
        }
        Brain.Index();
        return written;
    }

    private static void Replace(string file, string text)
    {
        string temp = file + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
    }
}
