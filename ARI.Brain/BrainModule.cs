using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases);
public record SearchResult(Note Note, double Score, int TermsMatched);
public record RecallPath(Note From, Note To, IReadOnlyList<Note> Notes);
public record RecallResult(IReadOnlyList<SearchResult> Candidates, IReadOnlyList<RecallPath> Paths);

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
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Brain");
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

        Dictionary<long, SearchResult> merged = new();
        void MergeIn(IEnumerable<SearchResult> batch)
        {
            foreach (SearchResult candidate in batch)
                if (!merged.TryGetValue(candidate.Note.id, out SearchResult? existing) || candidate.Score > existing.Score)
                    merged[candidate.Note.id] = candidate;
        }

        List<SearchResult> direct = Search(terms, seedNearLimit);
        MergeIn(direct);
        foreach (SearchResult seed in direct)
            MergeIn(SearchNear(seed.Note, terms, hopLimit, seedNearLimit));

        List<RecallPath> paths = Pathfind(terms, hopLimit, merged);

        List<SearchResult> ranked = merged.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.TermsMatched).Take(topLimit).ToList();
        return new RecallResult(ranked, paths);
    }

    // One anchor per distinct term, not per candidate — connects "the [REDACT] thing" to "the [REDACT] thing"
    // instead of cross-linking every matched note (which is mostly noise on a dense graph). Boosts each
    // connecting note's score in place, at most once per note regardless of how many paths cross it.
    private static List<RecallPath> Pathfind(IReadOnlyList<string> terms, int hopLimit, Dictionary<long, SearchResult> scores)
    {
        List<Note> anchors = terms
            .Select(term => Search(new List<string> { term }, 1).FirstOrDefault()?.Note)
            .Where(note => note is not null)
            .Select(note => note!)
            .DistinctBy(note => note.id)
            .ToList();

        List<RecallPath> paths = FindConnectingPaths(anchors, hopLimit);
        HashSet<long> boosted = new();
        foreach (RecallPath path in paths)
            foreach (Note note in path.Notes)
                if (boosted.Add(note.id) && scores.TryGetValue(note.id, out SearchResult? existing))
                    scores[note.id] = existing with { Score = existing.Score + PATH_BONUS };

        return paths;
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
        foreach (EngramAdd add in adds) WriteNamed(add.NoteName, add.Content, add.Aliases);
        Index();
    }

    public static void EditNotes(IReadOnlyList<EngramEdit> edits)
    {
        foreach (EngramEdit edit in edits)
        {
            if (edit.NewNoteName is null || edit.NewNoteName == edit.NoteName)
            {
                WriteNamed(edit.NoteName, edit.Content, edit.Aliases);
                continue;
            }
            Note? old = GetNote(edit.NoteName);
            List<string> aliases = new(edit.Aliases);
            if (old is not null)
            {
                string oldTitle = old.Title;
                string newTitle = edit.NewNoteName.Contains('/') ? edit.NewNoteName[(edit.NewNoteName.LastIndexOf('/') + 1)..] : edit.NewNoteName;
                if (!string.Equals(oldTitle, newTitle, StringComparison.OrdinalIgnoreCase) &&
                    !aliases.Contains(oldTitle, StringComparer.OrdinalIgnoreCase))
                    aliases.Add(oldTitle);
                File.Delete(Path.Combine(VaultRoot, old.Path));
            }
            Note.Write(PathFor(edit.NewNoteName), edit.Content, aliases, null);
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

    private static void WriteNamed(string name, string content, IReadOnlyList<string> aliases)
    {
        string bareTitle = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        Note? existing = GetNote(bareTitle);
        if (existing is not null)
            Note.Write(existing.Path, content, MergedAliases(existing, aliases), null);
        else
            Note.Write(PathFor(name), content, aliases, null);
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

    internal static string PathFor(string noteName)
    {
        const int MAX_SEGMENT_LENGTH = 120;
        IEnumerable<string> segments = noteName.Split('/')
            .Select(segment => invalidFileChars.Replace(segment, "").Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment => segment.Length > MAX_SEGMENT_LENGTH ? segment[..MAX_SEGMENT_LENGTH] : segment);
        return string.Join('/', segments) + ".md";
    }
}
