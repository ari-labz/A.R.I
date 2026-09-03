using System.Text.RegularExpressions;
using ARI.Common;

namespace ARI.BrainVault;

public record IndexStats(int Notes, int Edges, int Aliases, int Thoughts, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases, IReadOnlyList<string> SkippedNotes);
public record SearchResult(Note Note, double Score, int TermsMatched);
public record RecallPath(Note From, Note To, IReadOnlyList<Note> Notes);
public record RecallResult(IReadOnlyList<SearchResult> Candidates, IReadOnlyList<RecallPath> Paths);
public record ThoughtRecord(string Kind, string SpanText, string Comment, string Confidence, string Created);

// The brain's own database: every read and write to a note goes through here, never through a raw
// filesystem tool. A note is a structured thing (frontmatter fields, aliases, thoughts, links) —
// Brain.AddNote/EditNote know that shape and touch only the fields they're given; a generic file
// write does not, and will destroy whatever it wasn't told about.
public static class Brain
{
    private const double PATH_BONUS = 60.0;

    private static readonly Regex wikiLink = new(@"\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex invalidFileChars = new(@"[/\\:""?*|<>]", RegexOptions.Compiled);

    public static bool Ready { get; private set; }
    public static string VaultRoot { get; private set; } = string.Empty;
    public static string VaultName { get; private set; } = string.Empty;

    public static IndexStats Initialize(BrainConfig config)
    {
        VaultRoot = config.VaultPath.Length > 0
            ? config.VaultPath
            : Paths.Brain;
        VaultName = Path.GetFileName(VaultRoot);
        Database.Path = Path.Combine(VaultRoot, ".ari", "index.db");
        BrainBackup.Path_ = config.BackupPath;
        BrainBackup.MaxBackups = config.MaxBackups;
        Directory.CreateDirectory(VaultRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(Database.Path)!);
        IndexStats stats = Index();
        Ready = true;
        return stats;
    }

    // Rebuilds the index from the vault files. Safe to run at any time — the index is disposable.
    public static IndexStats Index()
    {
        List<(string Path, Note.Parsed Parsed, DateTime Updated)> files = new();
        foreach (string file in Directory.EnumerateFiles(VaultRoot, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(VaultRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            files.Add((relative, Note.Parse(File.ReadAllText(file)), File.GetLastWriteTimeUtc(file)));
        }
        return Database.Rebuild(files);
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────

    public static Note? GetNote(string name) => Database.FindNote(name);

    public static List<string> GetTitles() => Database.AllTitles();

    public static List<string> GetPaths() => Database.AllPaths();

    public static List<string> GetTitlesByFolder(string folderPath) => Database.TitlesInFolder(folderPath);

    public static Dictionary<string, List<string>> GetAliases()
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string title, string alias) in Database.AliasPairs())
        {
            if (!result.TryGetValue(title, out List<string>? list)) result[title] = list = new List<string>();
            list.Add(alias);
        }
        return result;
    }

    public static List<string> GetWikilinks(string markdown)
    {
        List<string> links = new();
        foreach (Match match in wikiLink.Matches(markdown))
        {
            string target = match.Groups[1].Value.Trim();
            if (target.Length > 0 && !links.Contains(target, StringComparer.OrdinalIgnoreCase)) links.Add(target);
        }
        return links;
    }

    // Rewrites every [[fromTitle]] / [[fromTitle|display]] across the vault to point at toTitle,
    // preserving any display alias. Called after a rename or merge so referrers never keep pointing
    // at a name that is now only an alias. Returns the number of files changed. Does not reindex —
    // the caller reindexes once after its structural changes.
    public static int RepointReferences(string fromTitle, string toTitle)
    {
        if (string.IsNullOrWhiteSpace(fromTitle) || string.IsNullOrWhiteSpace(toTitle)) return 0;
        if (string.Equals(fromTitle, toTitle, StringComparison.OrdinalIgnoreCase)) return 0;
        Regex pattern = new(@"\[\[" + Regex.Escape(fromTitle) + @"(\|[^\]]*)?\]\]", RegexOptions.IgnoreCase);
        int changed = 0;
        foreach (string file in Directory.EnumerateFiles(VaultRoot, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(VaultRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            string text = File.ReadAllText(file);
            string updated = pattern.Replace(text, match => $"[[{toTitle}{match.Groups[1].Value}]]");
            if (updated == text) continue;
            string temp = $"{file}.tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, file, overwrite: true);
            changed++;
        }
        return changed;
    }

    // Terminal guardrail: after a sweep, any [[link]] that resolves to no title, alias, or path is
    // genuinely dead (aliases already resolved, e.g. [[Al]] -> Alex, so those survive). De-link it
    // to its plain display text rather than leave a broken reference. Returns files changed; reindexes.
    public static int StripUnresolvedLinks()
    {
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (string title in Database.AllTitles()) known.Add(title);
        foreach ((string _, string alias) in Database.AliasPairs()) known.Add(alias);
        foreach (string path in Database.AllPaths()) known.Add(path);

        Regex link = new(@"\[\[([^\]|]+?)(\|[^\]]*)?\]\]", RegexOptions.Compiled);
        int changed = 0;
        foreach (string file in Directory.EnumerateFiles(VaultRoot, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(VaultRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            string text = File.ReadAllText(file);
            string updated = link.Replace(text, match =>
            {
                string target = match.Groups[1].Value.Trim();
                if (known.Contains(target)) return match.Value;
                return match.Groups[2].Success ? match.Groups[2].Value.TrimStart('|') : target; // keep the words, drop the brackets
            });
            if (updated == text) continue;
            string temp = $"{file}.tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, file, overwrite: true);
            changed++;
        }
        if (changed > 0) Index();
        return changed;
    }

    // ── Thoughts (margin annotations) ───────────────────────────────────────────────

    public static void AddThought(string noteName, string spanText, string comment, string confidence, string kind) =>
        GetNote(noteName)?.AddThought(spanText, comment, confidence, kind);

    public static List<ThoughtRecord> GetThoughts(string noteName)
    {
        Note? note = GetNote(noteName);
        return note is null ? new List<ThoughtRecord>() : Database.ThoughtsForNote(note.id);
    }

    public static List<(string NoteTitle, ThoughtRecord Thought)> GetRecentThoughts(int limit = 20) => Database.RecentThoughts(limit);

    // ── Search ───────────────────────────────────────────────────────────────────────

    public static List<SearchResult> Search(IReadOnlyList<string> terms, int resultLimit = 25) => Database.Search(terms, resultLimit);

    public static List<SearchResult> SearchNear(Note anchor, IReadOnlyList<string> terms, int maxJumps = 3, int resultLimit = 25) =>
        Database.SearchNear(anchor.id, terms, maxJumps, resultLimit);

    // One recursive walk per seed, meet-in-the-middle on the intersection.
    public static List<RecallPath> FindConnectingPaths(IReadOnlyList<Note> seeds, int maxJumps = 3)
    {
        if (seeds.Count < 2) return new List<RecallPath>();

        Dictionary<Note, Dictionary<long, (int Depth, long? Predecessor)>> reach = new();
        foreach (Note seed in seeds) reach[seed] = Database.Reachability(seed.id, maxJumps);

        List<RecallPath> paths = new();
        for (int i = 0; i < seeds.Count; i++)
        {
            for (int j = i + 1; j < seeds.Count; j++)
            {
                long? meetingPoint = reach[seeds[i]].Keys.Intersect(reach[seeds[j]].Keys)
                    .OrderBy(id => reach[seeds[i]][id].Depth + reach[seeds[j]][id].Depth)
                    .Cast<long?>().FirstOrDefault();
                if (meetingPoint is null) continue;

                List<Note> notes = WalkBack(reach[seeds[i]], meetingPoint.Value);
                notes.Reverse();
                notes.AddRange(WalkBack(reach[seeds[j]], meetingPoint.Value).Skip(1));
                paths.Add(new RecallPath(seeds[i], seeds[j], notes));
            }
        }
        return paths;
    }

    public static RecallResult Recall(IReadOnlyList<string> terms, int hopLimit = 3, int seedNearLimit = 25, int topLimit = 25)
    {
        if (terms.Count == 0) return new RecallResult(new List<SearchResult>(), new List<RecallPath>());

        List<SearchResult> directResults = Search(terms, seedNearLimit);

        List<SearchResult> indirectResults = new();
        foreach (SearchResult seed in directResults)
            indirectResults.AddRange(SearchNear(seed.Note, terms, hopLimit, seedNearLimit));

        List<SearchResult> combinedResults = new();
        combinedResults.AddRange(directResults);
        combinedResults.AddRange(indirectResults);
        List<SearchResult> allResults = Dedup(combinedResults);

        (List<SearchResult> boosted, List<RecallPath> paths) = Pathfind(terms, hopLimit, allResults);

        List<SearchResult> ranked = boosted.OrderByDescending(c => c.Score).ThenByDescending(c => c.TermsMatched).Take(topLimit).ToList();
        return new RecallResult(ranked, paths);
    }

    // Keeps the highest-scoring result per note across every batch — a note found by both the
    // direct search and several SearchNear calls counts once, at its best score.
    private static List<SearchResult> Dedup(IEnumerable<SearchResult> results)
    {
        Dictionary<long, SearchResult> best = new();
        foreach (SearchResult candidate in results)
            if (!best.TryGetValue(candidate.Note.id, out SearchResult? existing) || candidate.Score > existing.Score)
                best[candidate.Note.id] = candidate;
        return best.Values.ToList();
    }

    // One anchor per distinct term, not per result — connects "the alex thing" to "the jordan thing"
    // instead of cross-linking every matched note (which is mostly noise on a dense graph). Each note
    // on a connecting path is boosted once, regardless of how many paths cross it.
    private static (List<SearchResult> Boosted, List<RecallPath> Paths) Pathfind(IReadOnlyList<string> terms, int hopLimit, List<SearchResult> results)
    {
        List<Note> anchors = terms
            .Select(term => Search(new List<string> { term }, 1).FirstOrDefault()?.Note)
            .Where(note => note is not null)
            .Select(note => note!)
            .DistinctBy(note => note.id)
            .ToList();

        List<RecallPath> paths = FindConnectingPaths(anchors, hopLimit);
        HashSet<long> onPath = paths.SelectMany(path => path.Notes).Select(note => note.id).ToHashSet();

        List<SearchResult> boosted = results
            .Select(result => onPath.Contains(result.Note.id) ? result with { Score = result.Score + PATH_BONUS } : result)
            .ToList();

        return (boosted, paths);
    }

    private static List<Note> WalkBack(Dictionary<long, (int Depth, long? Predecessor)> reach, long from)
    {
        List<long> ids = new();
        long? current = from;
        while (current is not null)
        {
            ids.Add(current.Value);
            current = reach[current.Value].Predecessor;
        }
        return ids.Select(Database.NoteById).ToList();
    }

    // ── Writes (file first, then reindex) ───────────────────────────────────────────

    // Creates a note, or extends it if one already exists for this name — either way returns the
    // resulting note. Fields left unset (type/keywords) stay whatever they already were; see
    // Note.Write's "sticky" behaviour. This is the only way a new note should ever be created.
    public static Note AddNote(string name, string content, IReadOnlyList<string> aliases, string? type = null, IReadOnlyList<string>? keywords = null, bool? isSensitive = null)
    {
        WriteNamed(name, content, aliases, type, keywords, isSensitive);
        Index();
        return GetNote(name)!;
    }

    // Updates an existing note in place, or renames it when newName names a different, valid title.
    // A malformed or empty newName (e.g. "People/") is never treated as a rename — that would delete
    // the note or rename it to an empty title — it's treated as an in-place edit instead, guarding
    // the vault against bad model output.
    public static Note EditNote(string name, string content, IReadOnlyList<string> aliases, string? newName = null, string? type = null, IReadOnlyList<string>? keywords = null, bool? isSensitive = null)
    {
        string newBareTitle = newName is null ? string.Empty
            : (newName.Contains('/') ? newName[(newName.LastIndexOf('/') + 1)..] : newName).Trim();
        string oldBareTitle = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        bool isRename = newBareTitle.Length > 0 && !string.Equals(newBareTitle, oldBareTitle, StringComparison.OrdinalIgnoreCase);

        if (!isRename)
        {
            WriteNamed(name, content, aliases, type, keywords, isSensitive);
            Index();
            return GetNote(name)!;
        }

        Note? old = GetNote(name);
        List<string> mergedAliases = new(aliases);
        string newContent = content;
        string? renamedFrom = null;
        if (old is not null)
        {
            if (!mergedAliases.Contains(old.Title, StringComparer.OrdinalIgnoreCase)) mergedAliases.Add(old.Title);
            newContent = Note.CarryThoughtsInto(old.Content, newContent);
            File.Delete(Path.Combine(VaultRoot, old.Path));
            renamedFrom = old.Title;
        }
        Note.Write(PathFor(newName!), newContent, mergedAliases, null, type, keywords is { Count: > 0 } ? keywords : null, isSensitive);
        // Rewrite every [[oldTitle]] in other notes to [[newBareTitle]] so a rename never leaves the
        // referrers pointing at the old name (the alias still resolves them, but the text is repointed).
        if (renamedFrom is not null) RepointReferences(renamedFrom, newBareTitle);
        Index();
        return GetNote(newName!)!;
    }

    public static bool MergeNotes(string fromName, string intoName)
    {
        Note? from = GetNote(fromName);
        Note? into = GetNote(intoName);
        if (from is null || into is null || from.id == into.id) return false;
        from.MergeInto(into);
        return true;
    }

    public static void DeleteNote(string name) => GetNote(name)?.Delete();

    public static int PurgeAllNotes()
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(VaultRoot, "*.md", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(VaultRoot, file).Split(Path.DirectorySeparatorChar).Any(segment => segment.StartsWith('.'))) continue;
            File.Delete(file);
            count++;
        }
        Index();
        return count;
    }

    // ── Dirty set (Refactor's work queue; lives in the index only) ─────────────────

    public static void MarkDirty(IEnumerable<string> titles) => Database.MarkDirty(titles);

    public static List<string> GetDirtyNotes() => Database.DirtyTitles();

    public static void ClearDirty(IEnumerable<string> titles) => Database.ClearDirty(titles);

    // ── Graph walk ────────────────────────────────────────────────────────────────────

    // Highest total-degree notes — the starting points for a walk, where sprawl concentrates.
    public static List<Note> TopDegreeSeeds(int limit) => Database.SeedsByDegree(limit);

    // Every note, ordered by degree DESC. Refactor re-sorts these by "least-recently refactored" but
    // keeps this degree order as its tiebreak, so the whole vault rotates through instead of the walk
    // re-picking the same high-degree hubs each run.
    public static List<Note> AllSeedsByDegree() => Database.SeedsByDegree(int.MaxValue);

    // Adjacency skeleton (path + [type] + inbound '<' / outbound '>' connections) for the neighbourhood
    // BFS-reachable from the seed within `depth` hops, capped at `cap` nodes. Null if the seed is unknown.
    public static string? Skeleton(string seedTitle, int depth = 6, int cap = 1000)
    {
        Note? seed = GetNote(seedTitle);
        return seed is null ? null : Database.Skeleton(seed.id, depth, cap);
    }

    // ── Internal ────────────────────────────────────────────────────────────────────

    private static void WriteNamed(string name, string content, IReadOnlyList<string> aliases, string? type = null, IReadOnlyList<string>? keywords = null, bool? isSensitive = null)
    {
        string bareTitle = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        Note? existing = GetNote(bareTitle);
        if (existing is not null)
            Note.Write(existing.Path, Note.CarryThoughtsInto(existing.Content, content), MergedAliases(existing, aliases), null, type, keywords, isSensitive);
        else
            Note.Write(PathFor(name), content, aliases, null, type, keywords, isSensitive);
    }

    private static List<string> MergedAliases(Note note, IReadOnlyList<string> incoming)
    {
        List<string> merged = new(note.Aliases);
        foreach (string alias in incoming)
            if (!merged.Contains(alias, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(alias, note.Title, StringComparison.OrdinalIgnoreCase))
                merged.Add(alias);
        return merged;
    }

    private static readonly Regex bareDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    internal static string PathFor(string noteName)
    {
        const int MAX_SEGMENT_LENGTH = 120;
        // A bare date is always a daily conversation log — file it under Conversations/ deterministically,
        // regardless of whether the model prefixed the folder.
        if (bareDate.IsMatch(noteName.Trim())) noteName = "Conversations/" + noteName.Trim();
        IEnumerable<string> segments = noteName.Split('/')
            .Select(segment => invalidFileChars.Replace(segment, "").Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment => segment.Length > MAX_SEGMENT_LENGTH ? segment[..MAX_SEGMENT_LENGTH] : segment);
        return string.Join('/', segments) + ".md";
    }
}
