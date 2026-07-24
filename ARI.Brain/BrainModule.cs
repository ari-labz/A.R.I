using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, int Thoughts, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases, IReadOnlyList<string> SkippedNotes);
public record SearchResult(Note Note, double Score, int TermsMatched);
public record RecallPath(Note From, Note To, IReadOnlyList<Note> Notes);
public record RecallResult(IReadOnlyList<SearchResult> Candidates, IReadOnlyList<RecallPath> Paths);
public record ThoughtRecord(string Kind, string SpanText, string Comment, string Confidence, string Created);

public static class BrainModule
{
    private const string BACKUP_PREFIX = "ARI-Brain-";
    private const double PATH_BONUS = 60.0;

    private static readonly Regex wikiLink = new(@"\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex invalidFileChars = new(@"[/\\:""?*|<>]", RegexOptions.Compiled);

    private static string backupPath = "./Backups";
    private static int maxBackups = 5;

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
        backupPath = config.BackupPath;
        maxBackups = config.MaxBackups;
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

    public static Note CreateNote(string name, string content, IReadOnlyList<string> aliases)
    {
        Note? existing = GetNote(name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name) ?? GetNote(name);
        if (existing is not null)
        {
            existing.Save(content, MergedAliases(existing, aliases));
            return GetNote(existing.Title)!;
        }
        Note.Write(PathFor(name), content, aliases, null);
        Index();
        return GetNote(name)!;
    }

    public static void AddNotes(IReadOnlyList<EngramAdd> adds)
    {
        foreach (EngramAdd add in adds) WriteNamed(add.NoteName, add.Content, add.Aliases, add.Type);
        Index();
    }

    public static void EditNotes(IReadOnlyList<EngramEdit> edits)
    {
        foreach (EngramEdit edit in edits)
        {
            // A rename only happens when NewNoteName names a DIFFERENT, valid title. A malformed or
            // empty newName (e.g. "People/") must never delete the note or rename to an empty title —
            // treat it as an in-place edit. This guards the vault against bad model output.
            string newBareTitle = edit.NewNoteName is null ? string.Empty
                : (edit.NewNoteName.Contains('/') ? edit.NewNoteName[(edit.NewNoteName.LastIndexOf('/') + 1)..] : edit.NewNoteName).Trim();
            string oldBareTitle = edit.NoteName.Contains('/') ? edit.NoteName[(edit.NoteName.LastIndexOf('/') + 1)..] : edit.NoteName;
            bool isRename = newBareTitle.Length > 0 && !string.Equals(newBareTitle, oldBareTitle, StringComparison.OrdinalIgnoreCase);

            if (!isRename)
            {
                WriteNamed(edit.NoteName, edit.Content, edit.Aliases, edit.Type);
                continue;
            }
            Note? old = GetNote(edit.NoteName);
            List<string> aliases = new(edit.Aliases);
            string newContent = edit.Content;
            string? renamedFrom = null;
            if (old is not null)
            {
                if (!aliases.Contains(old.Title, StringComparer.OrdinalIgnoreCase)) aliases.Add(old.Title);
                newContent = Note.CarryThoughtsInto(old.Content, newContent);
                File.Delete(Path.Combine(VaultRoot, old.Path));
                renamedFrom = old.Title;
            }
            Note.Write(PathFor(edit.NewNoteName!), newContent, aliases, null, edit.Type);
            // Rewrite every [[oldTitle]] in other notes to [[newBareTitle]] so a rename never leaves the
            // referrers pointing at the old name (the alias still resolves them, but the text is repointed).
            if (renamedFrom is not null) RepointReferences(renamedFrom, newBareTitle);
        }
        Index();
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

    // ── Structural maintenance ──────────────────────────────────────────────────────

    // A folder full of notes needs a hub note beside it (Pets/ → Pets.md) to index them. Engram places
    // members correctly but doesn't always create the hub note; this makes it deterministic.
    public static int EnsureHubNotes()
    {
        int created = 0;
        foreach (string dir in Directory.EnumerateDirectories(VaultRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(VaultRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            if (!Directory.EnumerateFiles(dir, "*.md").Any()) continue;   // no direct child notes → not a hub
            if (File.Exists(dir + ".md")) continue;                        // hub note already exists

            string name = Path.GetFileName(dir);
            Note.Write($"{relative}.md", $"# {name}\n\nHub for {name}.\n\n## Changelog\n\n- {DateTime.UtcNow:yyyy-MM-dd}: Created hub note.\n",
                Array.Empty<string>(), null);
            created++;
        }
        if (created > 0) Index();
        return created;
    }

    // Merges a drifted title variant ("Alex — User", "Jordan - partner") back into its base note when
    // the base exists — the write phase sometimes appends a descriptor to a resolved note's title, which
    // would otherwise leave a duplicate. The variant title becomes an alias on the base (via MergeNotes).
    public static int MergeTitleVariants()
    {
        int merged = 0;
        List<string> titles = Database.AllTitles();
        HashSet<string> titleSet = new(titles, StringComparer.OrdinalIgnoreCase);
        foreach (string title in titles)
        {
            int cut = title.IndexOf(" — ", StringComparison.Ordinal);
            if (cut < 0) cut = title.IndexOf(" - ", StringComparison.Ordinal);
            if (cut <= 0) continue;
            string basePart = title[..cut].Trim();
            if (basePart.Length > 0 && !basePart.Equals(title, StringComparison.OrdinalIgnoreCase) && titleSet.Contains(basePart))
                if (MergeNotes(title, basePart)) merged++;
        }
        return merged;
    }

    public static int EnsureHubChildLinks()
    {
        int updated = 0;
        foreach (Note hub in Database.AllNotes())
        {
            if (!hub.HasChildren()) continue;
            string content = hub.Content;
            List<string> linked = GetWikilinks(content);
            List<Note> missing = hub.GetChildren()
                .Where(child => !linked.Contains(child.Title, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count == 0) continue;

            string additions = string.Join('\n', missing.Select(child => $"- [[{child.Title}]]"));
            content = content.Contains("## Members")
                ? content.Replace("## Members", $"## Members\n{additions}")
                : $"{content.TrimEnd()}\n\n## Members\n{additions}\n";
            Note.Write(hub.Path, content, hub.Aliases, null);
            updated++;
        }
        if (updated > 0) Index();
        return updated;
    }

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

    public static int CleanUnknownStubs()
    {
        int cleaned = 0;
        foreach (Note stub in Database.UnknownStubs())
        {
            Note? owner = Database.AliasOwner(stub.Title, stub.id);
            if (owner is null) continue;
            stub.MergeInto(owner);
            cleaned++;
        }
        return cleaned;
    }

    // ── Backup ──────────────────────────────────────────────────────────────────────

    private record BackupNote(string Title, string Folder, string Content, IReadOnlyList<string>? Aliases);

    public static string Backup()
    {
        Directory.CreateDirectory(backupPath);
        List<BackupNote> notes = Database.AllNotes()
            .Select(note => new BackupNote(note.Title, note.Folder, note.ToPrompt(), note.Aliases))
            .ToList();

        string json = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, noteCount = notes.Count, notes },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string fileName = $"{BACKUP_PREFIX}{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.zip";
        string zipPath = Path.Combine(backupPath, fileName);
        using (FileStream stream = File.Create(zipPath))
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
        using (StreamWriter writer = new(zip.CreateEntry("brain.json").Open()))
            writer.Write(json);

        List<string> existing = Directory.EnumerateFiles(backupPath, $"{BACKUP_PREFIX}*.zip").OrderByDescending(f => f).ToList();
        foreach (string stale in existing.Skip(maxBackups)) File.Delete(stale);
        return $"Backed up {notes.Count} notes to {fileName}.";
    }

    public static List<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(backupPath)) return new List<BackupInfo>();
        List<BackupInfo> backups = new();
        foreach (string file in Directory.EnumerateFiles(backupPath, $"{BACKUP_PREFIX}*.zip").OrderByDescending(f => f))
        {
            using FileStream stream = File.OpenRead(file);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);
            using StreamReader reader = new(zip.GetEntry("brain.json")!.Open());
            JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            backups.Add(new BackupInfo(Path.GetFileName(file), File.GetCreationTimeUtc(file),
                new FileInfo(file).Length, document.RootElement.GetProperty("noteCount").GetInt32()));
        }
        return backups;
    }

    // Additive: recreates missing notes, overwrites existing ones, never deletes.
    public static string RestoreBackup(string fileName)
    {
        string zipPath = Path.Combine(backupPath, fileName);
        using FileStream stream = File.OpenRead(zipPath);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        using StreamReader reader = new(zip.GetEntry("brain.json")!.Open());
        JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());

        int restored = 0;
        foreach (JsonElement element in document.RootElement.GetProperty("notes").EnumerateArray())
        {
            string title = element.GetProperty("title").GetString()!;
            string folder = element.GetProperty("folder").GetString() ?? string.Empty;
            string content = element.GetProperty("content").GetString()!;
            List<string> aliases = element.TryGetProperty("aliases", out JsonElement aliasElement) && aliasElement.ValueKind == JsonValueKind.Array
                ? aliasElement.EnumerateArray().Select(a => a.GetString()!).ToList()
                : new List<string>();

            int bodyStart = content.StartsWith("Path: ") ? content.IndexOf('\n') + 1 : 0;
            Note.Write(PathFor(folder.Length > 0 ? $"{folder}/{title}" : title), content[bodyStart..].TrimStart('\n'), aliases, null);
            restored++;
        }
        Index();
        return $"Restored {restored} notes from {fileName}.";
    }

    // ── Internal ────────────────────────────────────────────────────────────────────

    private static void WriteNamed(string name, string content, IReadOnlyList<string> aliases, string? type = null)
    {
        string bareTitle = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        Note? existing = GetNote(bareTitle);
        if (existing is not null)
            Note.Write(existing.Path, Note.CarryThoughtsInto(existing.Content, content), MergedAliases(existing, aliases), null, type);
        else
            Note.Write(PathFor(name), content, aliases, null, type);
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
