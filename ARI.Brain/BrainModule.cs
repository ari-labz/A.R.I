using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases);

/// <summary>One search hit. Match explains why it matched; Distance is jumps from the anchor (0 unless SearchNear).</summary>
public record SearchResult(Note Note, string Match, int Distance);

/// <summary>
/// Ari's brain: a vault of markdown files (the source of truth) indexed by SQLite.
/// Reads navigate to Note objects; writes go file-first, then the index refreshes.
/// </summary>
public static class BrainModule
{
    private const string BACKUP_PREFIX = "ARI-Brain-";

    private static readonly Regex wikiLink = new(@"\[\[([^\]|]+?)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex invalidFileChars = new(@"[/\\:""?*|<>]", RegexOptions.Compiled);
    private static readonly string[] MATCH_NAMES = ["", "title", "alias", "title-partial", "alias-partial", "content"];

    // Ranks matches into tiers so title hits always sort above content mentions.
    private const string MATCH_TIERS = """
        SELECT n.noteID, n.title, n.path, 1 AS tier, 0.0 AS score FROM notes n WHERE n.title = $term
        UNION ALL
        SELECT n.noteID, n.title, n.path, 2, 0.0 FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $term
        UNION ALL
        SELECT n.noteID, n.title, n.path, 3, 0.0 FROM notes n WHERE n.title LIKE $pattern ESCAPE '\'
        UNION ALL
        SELECT n.noteID, n.title, n.path, 4, 0.0 FROM aliases a JOIN notes n USING(noteID) WHERE a.alias LIKE $pattern ESCAPE '\'
        UNION ALL
        SELECT n.noteID, n.title, n.path, 5, bm25(note_search) FROM note_search JOIN notes n ON n.noteID = note_search.rowid WHERE note_search MATCH $phrase
        """;

    private static string backupPath = "./Backups";
    private static int maxBackups = 5;

    public static bool Ready { get; private set; }

    public static string VaultRoot { get; private set; } = string.Empty;

    public static string VaultName { get; private set; } = string.Empty;

    /// <summary>Points the module at the vault and builds the index. Must be called before anything else.</summary>
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

    /// <summary>Rebuilds the index database from the vault files. Safe to run at any time; the index is disposable.</summary>
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

    /// <summary>Resolves a title, alias, or full "Folder/Title" name to its Note. Null when unknown.</summary>
    public static Note? GetNote(string name) => Database.QueryNotes("""
        SELECT noteID, title, path FROM notes WHERE title = $name
        UNION SELECT n.noteID, n.title, n.path FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $name
        UNION SELECT noteID, title, path FROM notes WHERE substr(path, 1, length(path) - 3) = $name
        LIMIT 1
        """, ("$name", name)).FirstOrDefault();

    public static List<string> GetTitles() => Database.Column("SELECT title FROM notes ORDER BY title");

    /// <summary>Full note names in "Folder/Sub/Title" form.</summary>
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

    /// <summary>Extracts [[wikilink]] targets from markdown, deduplicated, in order of first appearance.</summary>
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

    /// <summary>Every note matching the term, best first: exact title, alias, partial title/alias, then content mentions.</summary>
    public static List<SearchResult> Search(string searchTerm, int resultLimit = 20) => Matches($"""
        SELECT noteID, title, path, MIN(tier) AS bestTier, MIN(score) AS bestScore, 0 AS distance
        FROM ({MATCH_TIERS}) GROUP BY noteID
        ORDER BY bestTier, bestScore LIMIT $limit
        """, searchTerm, resultLimit);

    /// <summary>Search restricted to notes within maxJumps links (either direction) of the anchor note.</summary>
    public static List<SearchResult> SearchNear(string anchorTitle, string searchTerm, int maxJumps, int resultLimit = 20)
    {
        Note? anchor = GetNote(anchorTitle);
        if (anchor is null) return new List<SearchResult>();

        return Matches($"""
            WITH RECURSIVE hop(noteID, depth) AS (
                SELECT $anchorId, 0
                UNION
                SELECT CASE WHEN c.noteIDFrom = hop.noteID THEN c.noteIDTo ELSE c.noteIDFrom END, hop.depth + 1
                FROM connections c, hop
                WHERE hop.depth < $maxJumps AND hop.noteID IN (c.noteIDFrom, c.noteIDTo)
            ),
            near(noteID, distance) AS (SELECT noteID, MIN(depth) FROM hop GROUP BY noteID)
            SELECT noteID, title, path, MIN(tier) AS bestTier, MIN(score) AS bestScore, near.distance
            FROM ({MATCH_TIERS}) matches JOIN near USING(noteID) GROUP BY noteID
            ORDER BY bestTier, near.distance, bestScore LIMIT $limit
            """, searchTerm, resultLimit, ("$anchorId", anchor.id), ("$maxJumps", maxJumps));
    }

    private static List<SearchResult> Matches(string sql, string searchTerm, int resultLimit, params (string Name, object Value)[] extras)
    {
        (string Name, object Value)[] parameters = new (string, object)[]
        {
            ("$term", searchTerm),
            ("$pattern", $"%{searchTerm.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"),
            ("$phrase", $"\"{searchTerm.Replace("\"", "\"\"")}\""),
            ("$limit", resultLimit),
        }.Concat(extras).ToArray();

        List<SearchResult> results = new();
        foreach ((Note note, int tier, int distance) in Database.QueryHits(sql, parameters))
            results.Add(new SearchResult(note, MATCH_NAMES[tier], distance));
        return results;
    }

    // ── Writes (file first, then reindex) ───────────────────────────────────────────

    /// <summary>Creates a note at "Folder/Title" — or updates it if the name already resolves to one.</summary>
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

    /// <summary>Batch create/update from an Engram sweep. One reindex at the end.</summary>
    public static void AddNotes(IReadOnlyList<EngramAdd> adds)
    {
        foreach (EngramAdd add in adds) WriteNamed(add.NoteName, add.Content, add.Aliases);
        Index();
    }

    /// <summary>Batch edit from an Engram sweep. A NewNoteName moves the file; the old title survives as an alias.</summary>
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

    /// <summary>Deterministic hub pass: every folder note links to each of its direct children.</summary>
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

    /// <summary>Folds Unknown/ stubs whose name is already an alias of a real note into that note.</summary>
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

    /// <summary>Additive restore: recreates missing notes and overwrites existing ones. Never deletes.</summary>
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

    // Create-or-update without reindexing; batch operations reindex once at the end.
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

    /// <summary>Converts a note name like "People/[REDACT]'s Family/[REDACT]" into a safe vault-relative file path.</summary>
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
