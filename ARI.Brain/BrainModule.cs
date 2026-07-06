using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases);
public record RecallCandidate(Note Note, double Score, int TermsMatched);
public record RecallPath(Note From, Note To, IReadOnlyList<Note> Notes);
public record RecallResult(IReadOnlyList<RecallCandidate> Candidates, IReadOnlyList<RecallPath> Paths);

public static class BrainModule
{
    private const string BACKUP_PREFIX = "ARI-Brain-";

    private static readonly Regex wikiLink = new(@"\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex invalidFileChars = new(@"[/\\:""?*|<>]", RegexOptions.Compiled);

    // Tier weight per term match: title/alias hits dominate, a bare content mention barely
    // registers alone but adds up across several terms.
    private const double TIER_TITLE          = 100.0;
    private const double TIER_ALIAS          = 90.0;
    private const double TIER_TITLE_PARTIAL  = 50.0;
    private const double TIER_ALIAS_PARTIAL  = 40.0;
    private const double TIER_CONTENT        = 10.0;
    private const double PATH_BONUS          = 60.0;

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

        List<string> dirtyBefore = File.Exists(Database.Path) && new FileInfo(Database.Path).Length > 0
            ? GetDirtyNotes() : new List<string>();

        using SqliteConnection db = Database.Open();
        Database.Run(db, "PRAGMA foreign_keys = ON;");
        using SqliteTransaction transaction = db.BeginTransaction();
        Database.Run(db, Database.SCHEMA);

        Dictionary<string, long> idsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, Note.Parsed parsed, DateTime updated) in files)
        {
            string title = Path.GetFileNameWithoutExtension(path);
            Database.Run(db, "INSERT INTO notes(title, path, content, updated) VALUES ($title, $path, $content, $updated)",
                ("$title", title), ("$path", path), ("$content", parsed.Body),
                ("$updated", updated.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            idsByName[title] = Database.LastId(db);
        }

        int aliasCount = 0;
        List<string> skippedAliases = new();
        foreach ((string path, Note.Parsed parsed, DateTime _) in files)
        {
            string title = Path.GetFileNameWithoutExtension(path);
            foreach (string alias in parsed.AliasList)
            {
                if (idsByName.ContainsKey(alias))
                {
                    skippedAliases.Add($"{title}: alias '{alias}' shadows an existing name");
                    continue;
                }
                Database.Run(db, "INSERT INTO aliases(alias, noteID) VALUES ($alias, $id)", ("$alias", alias), ("$id", idsByName[title]));
                idsByName[alias] = idsByName[title];
                aliasCount++;
            }
        }

        int edgeCount = 0;
        List<string> unresolved = new();
        foreach ((string path, Note.Parsed parsed, DateTime _) in files)
        {
            long source = idsByName[Path.GetFileNameWithoutExtension(path)];
            foreach (string target in GetWikilinks(parsed.Body))
            {
                if (!idsByName.TryGetValue(target, out long destination))
                {
                    unresolved.Add($"{Path.GetFileNameWithoutExtension(path)} -> {target}");
                    continue;
                }
                if (destination == source) continue;
                Database.Run(db, "INSERT OR IGNORE INTO connections(noteIDFrom, noteIDTo) VALUES ($from, $to)",
                    ("$from", source), ("$to", destination));
                edgeCount++;
            }
        }

        Database.Run(db, "INSERT INTO note_search(rowid, title, content) SELECT noteID, title, content FROM notes");
        foreach (string title in dirtyBefore)
            Database.Run(db, "UPDATE notes SET dirty = 1 WHERE title = $title", ("$title", title));
        transaction.Commit();

        return new IndexStats(files.Count, edgeCount, aliasCount, unresolved, skippedAliases);
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────

    public static Note? GetNote(string name) => Database.QueryNotes("""
        SELECT noteID, title, path FROM notes WHERE title = $name
        UNION SELECT n.noteID, n.title, n.path FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $name
        UNION SELECT noteID, title, path FROM notes WHERE substr(path, 1, length(path) - 3) = $name
        LIMIT 1
        """, ("$name", name)).FirstOrDefault();

    public static List<string> GetTitles() => Database.Column("SELECT title FROM notes ORDER BY title");

    public static List<string> GetPaths() => Database.Column("SELECT substr(path, 1, length(path) - 3) FROM notes ORDER BY path");

    public static List<string> GetTitlesByFolder(string folderPath) =>
        Database.Column("SELECT title FROM notes WHERE path LIKE $inside AND path NOT LIKE $deeper ORDER BY title",
            ("$inside", folderPath.Length > 0 ? $"{folderPath}/%" : "%"),
            ("$deeper", folderPath.Length > 0 ? $"{folderPath}/%/%" : "%/%"));

    public static Dictionary<string, List<string>> GetAliases()
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string row in Database.Column("SELECT n.title || '/' || a.alias FROM aliases a JOIN notes n USING(noteID) ORDER BY n.title"))
        {
            string title = row[..row.IndexOf('/')];
            string alias = row[(row.IndexOf('/') + 1)..];
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

    public static List<RecallCandidate> Search(IReadOnlyList<string> terms, int resultLimit = 25)
    {
        string cte = HitsCte(terms, out (string Name, object Value)[] parameters);
        return Rank(cte, "hits", parameters, resultLimit);
    }

    public static List<RecallCandidate> SearchNear(Note anchor, IReadOnlyList<string> terms, int maxJumps = 3, int resultLimit = 25)
    {
        string hits = HitsCte(terms, out (string Name, object Value)[] parameters);
        string cte = $"""
            hop(noteID, depth) AS (
                SELECT $anchorId, 0
                UNION
                SELECT CASE WHEN c.noteIDFrom = hop.noteID THEN c.noteIDTo ELSE c.noteIDFrom END, hop.depth + 1
                FROM connections c, hop
                WHERE hop.depth < $maxJumps AND hop.noteID IN (c.noteIDFrom, c.noteIDTo)
            ),
            near(noteID) AS (SELECT DISTINCT noteID FROM hop),
            {hits},
            nearHits(term, noteID, tier) AS (SELECT h.term, h.noteID, h.tier FROM hits h JOIN near USING(noteID))
            """;
        parameters = parameters.Append(("$anchorId", anchor.id)).Append(("$maxJumps", (object)maxJumps)).ToArray();
        return Rank(cte, "nearHits", parameters, resultLimit);
    }

    // One recursive walk per seed, meet-in-the-middle on the intersection.
    public static List<RecallPath> FindConnectingPaths(IReadOnlyList<Note> seeds, int maxJumps = 3)
    {
        if (seeds.Count < 2) return new List<RecallPath>();

        Dictionary<Note, Dictionary<long, (int Depth, long? Predecessor)>> reach = new();
        foreach (Note seed in seeds) reach[seed] = Reachability(seed, maxJumps);

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
        if (terms.Count == 0) return new RecallResult(new List<RecallCandidate>(), new List<RecallPath>());

        Dictionary<long, RecallCandidate> merged = new();
        void MergeIn(IEnumerable<RecallCandidate> batch)
        {
            foreach (RecallCandidate candidate in batch)
                if (!merged.TryGetValue(candidate.Note.id, out RecallCandidate? existing) || candidate.Score > existing.Score)
                    merged[candidate.Note.id] = candidate;
        }

        List<RecallCandidate> direct = Search(terms, seedNearLimit);
        MergeIn(direct);
        foreach (RecallCandidate seed in direct)
            MergeIn(SearchNear(seed.Note, terms, hopLimit, seedNearLimit));

        // One anchor per distinct term, not per candidate — connects "the [REDACT] thing" to "the [REDACT]
        // thing" instead of cross-linking every matched note (which is mostly noise on a dense graph).
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
                if (boosted.Add(note.id) && merged.TryGetValue(note.id, out RecallCandidate? existing))
                    merged[note.id] = existing with { Score = existing.Score + PATH_BONUS };

        List<RecallCandidate> ranked = merged.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.TermsMatched).Take(topLimit).ToList();
        return new RecallResult(ranked, paths);
    }

    // Terms become rows in a joined table, not a C# loop — one round trip regardless of term count.
    private static string HitsCte(IReadOnlyList<string> terms, out (string Name, object Value)[] parameters)
    {
        List<string> termRows = new();
        List<(string, object)> termParameters = new();
        for (int i = 0; i < terms.Count; i++)
        {
            termRows.Add($"SELECT ${"term" + i} AS term, ${"pattern" + i} AS pattern, ${"phrase" + i} AS phrase");
            termParameters.Add(($"$term{i}", terms[i]));
            termParameters.Add(($"$pattern{i}", $"%{terms[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"));
            termParameters.Add(($"$phrase{i}", $"\"{terms[i].Replace("\"", "\"\"")}\""));
        }
        parameters = termParameters.ToArray();

        string termTable = string.Join("\nUNION ALL\n", termRows);
        return $"""
            termTable(term, pattern, phrase) AS ({termTable}),
            hits(term, noteID, tier) AS (
                SELECT t.term, n.noteID, 1 FROM notes n, termTable t WHERE n.title = t.term
                UNION ALL
                SELECT t.term, n.noteID, 2 FROM aliases a JOIN notes n USING(noteID), termTable t WHERE a.alias = t.term
                UNION ALL
                SELECT t.term, n.noteID, 3 FROM notes n, termTable t WHERE n.title LIKE t.pattern ESCAPE '\'
                UNION ALL
                SELECT t.term, n.noteID, 4 FROM aliases a JOIN notes n USING(noteID), termTable t WHERE a.alias LIKE t.pattern ESCAPE '\'
                UNION ALL
                SELECT t.term, ns.rowid, 5 FROM note_search ns JOIN termTable t ON note_search MATCH t.phrase
            )
            """;
    }

    // "WITH RECURSIVE" unconditionally — SQLite allows it even when nothing in the chain recurses,
    // so one code path serves both Search and SearchNear.
    private static List<RecallCandidate> Rank(string cte, string hitsTable, (string Name, object Value)[] parameters, int resultLimit)
    {
        string sql = $"""
            WITH RECURSIVE {cte},
            bestPerTerm(term, noteID, tier) AS (SELECT term, noteID, MIN(tier) FROM {hitsTable} GROUP BY term, noteID)
            SELECT n.noteID, n.title, n.path,
                   SUM(CASE bestPerTerm.tier
                       WHEN 1 THEN {TIER_TITLE} WHEN 2 THEN {TIER_ALIAS}
                       WHEN 3 THEN {TIER_TITLE_PARTIAL} WHEN 4 THEN {TIER_ALIAS_PARTIAL}
                       ELSE {TIER_CONTENT} END) AS score,
                   COUNT(DISTINCT bestPerTerm.term) AS termsMatched
            FROM bestPerTerm JOIN notes n USING(noteID)
            GROUP BY n.noteID
            ORDER BY score DESC, termsMatched DESC
            LIMIT $limit
            """;

        List<RecallCandidate> results = new();
        foreach ((Note note, double score, int termsMatched) in Database.QueryScored(sql, parameters.Append(("$limit", resultLimit)).ToArray()))
            results.Add(new RecallCandidate(note, score, termsMatched));
        return results;
    }

    private static Dictionary<long, (int Depth, long? Predecessor)> Reachability(Note seed, int maxJumps)
    {
        using SqliteConnection db = Database.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE hop(noteID, depth, predecessor) AS (
                SELECT $seedId, 0, NULL
                UNION
                SELECT CASE WHEN c.noteIDFrom = hop.noteID THEN c.noteIDTo ELSE c.noteIDFrom END, hop.depth + 1, hop.noteID
                FROM connections c, hop
                WHERE hop.depth < $maxJumps AND hop.noteID IN (c.noteIDFrom, c.noteIDTo)
            )
            SELECT noteID, MIN(depth) AS depth, predecessor FROM hop GROUP BY noteID
            """;
        command.Parameters.AddWithValue("$seedId", seed.id);
        command.Parameters.AddWithValue("$maxJumps", maxJumps);

        Dictionary<long, (int, long?)> result = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetInt64(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        return result;
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
        return ids.Select(id => Database.QueryNotes("SELECT noteID, title, path FROM notes WHERE noteID = $id", ("$id", id)).First()).ToList();
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

    public static void MarkDirty(IEnumerable<string> titles)
    {
        using SqliteConnection db = Database.Open();
        foreach (string title in titles)
            Database.Run(db, "UPDATE notes SET dirty = 1 WHERE title = $title OR noteID = (SELECT noteID FROM aliases WHERE alias = $title)",
                ("$title", title));
    }

    public static List<string> GetDirtyNotes() => Database.Column("SELECT title FROM notes WHERE dirty = 1 ORDER BY title");

    public static void ClearDirty(IEnumerable<string> titles)
    {
        using SqliteConnection db = Database.Open();
        foreach (string title in titles)
            Database.Run(db, "UPDATE notes SET dirty = 0 WHERE title = $title", ("$title", title));
    }

    // ── Structural maintenance ──────────────────────────────────────────────────────

    public static int EnsureHubChildLinks()
    {
        int updated = 0;
        foreach (Note hub in Database.QueryNotes("SELECT noteID, title, path FROM notes"))
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
        foreach (Note stub in Database.QueryNotes("SELECT noteID, title, path FROM notes WHERE path LIKE 'Unknown/%'"))
        {
            Note? owner = Database.QueryNotes(
                "SELECT n.noteID, n.title, n.path FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $title AND n.noteID != $id",
                ("$title", stub.Title), ("$id", stub.id)).FirstOrDefault();
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
        List<BackupNote> notes = Database.QueryNotes("SELECT noteID, title, path FROM notes")
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
