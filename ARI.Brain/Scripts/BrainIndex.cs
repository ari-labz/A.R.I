using Microsoft.Data.Sqlite;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases);

/// <summary>One search hit. Match explains why it matched; Distance is jumps from the anchor (0 unless SearchNear).</summary>
public record SearchResult(string Title, string Path, string Match, int Distance);

/// <summary>
/// SQLite index over the vault: pointers, graph edges, aliases, and full-text search.
/// The vault files are the source of truth — this database is derived and disposable.
/// </summary>
public class BrainIndex
{
    private const string SCHEMA = """
        DROP TABLE IF EXISTS note_search;
        DROP TABLE IF EXISTS annotations;
        DROP TABLE IF EXISTS aliases;
        DROP TABLE IF EXISTS connections;
        DROP TABLE IF EXISTS notes;
        CREATE TABLE notes (
            noteID   INTEGER PRIMARY KEY AUTOINCREMENT,
            title    TEXT NOT NULL UNIQUE COLLATE NOCASE,
            path     TEXT NOT NULL,
            content  TEXT NOT NULL,
            updated  TEXT NOT NULL,
            dirty    INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE connections (
            noteIDFrom INTEGER NOT NULL REFERENCES notes(noteID) ON DELETE CASCADE,
            noteIDTo   INTEGER NOT NULL REFERENCES notes(noteID) ON DELETE CASCADE,
            weight     REAL NOT NULL DEFAULT 1.0,
            PRIMARY KEY (noteIDFrom, noteIDTo)
        );
        CREATE INDEX idx_connections_to ON connections(noteIDTo);
        CREATE TABLE aliases (
            alias  TEXT NOT NULL UNIQUE COLLATE NOCASE,
            noteID INTEGER NOT NULL REFERENCES notes(noteID) ON DELETE CASCADE
        );
        CREATE TABLE annotations (
            annotationID INTEGER PRIMARY KEY AUTOINCREMENT,
            noteID       INTEGER NOT NULL REFERENCES notes(noteID) ON DELETE CASCADE,
            spanText     TEXT NOT NULL,
            comment      TEXT NOT NULL,
            confidence   TEXT NOT NULL,
            created      TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE note_search USING fts5(title, content, content='notes', content_rowid='noteID');
        """;

    private readonly VaultStore vault;
    private readonly string dbPath;

    public BrainIndex(VaultStore vault, string vaultPath)
    {
        this.vault = vault;
        dbPath = Path.Combine(vaultPath, ".ari", "index.db");
    }

    /// <summary>Rebuilds the database from the file vault. Safe to run at any time; the index is disposable.</summary>
    public IndexStats Index()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        List<VaultNote> notes = vault.ScanNotes();

        using SqliteConnection db = Open();
        Run(db, "PRAGMA foreign_keys = ON;");
        using SqliteTransaction transaction = db.BeginTransaction();
        Run(db, SCHEMA);

        Dictionary<string, long> idsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (VaultNote note in notes)
        {
            Run(db, "INSERT INTO notes(title, path, content, updated) VALUES ($title, $path, $content, $updated)",
                ("$title", note.Title), ("$path", note.Path), ("$content", note.Content),
                ("$updated", note.Updated.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            idsByName[note.Title] = LastId(db);
        }

        int aliasCount = 0;
        List<string> skippedAliases = new();
        foreach (VaultNote note in notes)
        {
            foreach (string alias in note.Aliases)
            {
                if (idsByName.ContainsKey(alias))
                {
                    skippedAliases.Add($"{note.Title}: alias '{alias}' shadows an existing name");
                    continue;
                }
                Run(db, "INSERT INTO aliases(alias, noteID) VALUES ($alias, $id)",
                    ("$alias", alias), ("$id", idsByName[note.Title]));
                idsByName[alias] = idsByName[note.Title];
                aliasCount++;
            }
        }

        int edgeCount = 0;
        List<string> unresolved = new();
        foreach (VaultNote note in notes)
        {
            long source = idsByName[note.Title];
            foreach (string target in VaultStore.ParseLinks(note.Content))
            {
                if (!idsByName.TryGetValue(target, out long destination))
                {
                    unresolved.Add($"{note.Title} -> {target}");
                    continue;
                }
                if (destination == source) continue;
                Run(db, "INSERT OR IGNORE INTO connections(noteIDFrom, noteIDTo) VALUES ($from, $to)",
                    ("$from", source), ("$to", destination));
                edgeCount++;
            }
        }

        Run(db, "INSERT INTO note_search(rowid, title, content) SELECT noteID, title, content FROM notes");
        transaction.Commit();

        return new IndexStats(notes.Count, edgeCount, aliasCount, unresolved, skippedAliases);
    }

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

    private static readonly string[] MATCH_NAMES = ["", "title", "alias", "title-partial", "alias-partial", "content"];

    /// <summary>Every note matching the term, best first: exact title, alias, partial title/alias, then content mentions.</summary>
    public List<SearchResult> Search(string searchTerm, int resultLimit = 20)
    {
        using SqliteConnection db = Open();
        return Matches(db, $"""
            SELECT title, path, MIN(tier) AS bestTier, MIN(score) AS bestScore, 0 AS distance
            FROM ({MATCH_TIERS}) GROUP BY noteID
            ORDER BY bestTier, bestScore LIMIT $limit
            """, searchTerm, resultLimit);
    }

    /// <summary>Search restricted to notes within maxJumps links (either direction) of the anchor note.</summary>
    public List<SearchResult> SearchNear(string anchorTitle, string searchTerm, int maxJumps, int resultLimit = 20)
    {
        using SqliteConnection db = Open();

        using SqliteCommand resolve = db.CreateCommand();
        resolve.CommandText = """
            SELECT noteID FROM notes WHERE title = $anchor
            UNION SELECT noteID FROM aliases WHERE alias = $anchor LIMIT 1
            """;
        resolve.Parameters.AddWithValue("$anchor", anchorTitle);
        object? anchorId = resolve.ExecuteScalar();
        if (anchorId is null) return new List<SearchResult>();

        return Matches(db, $"""
            WITH RECURSIVE hop(noteID, depth) AS (
                SELECT $anchorId, 0
                UNION
                SELECT CASE WHEN c.noteIDFrom = hop.noteID THEN c.noteIDTo ELSE c.noteIDFrom END, hop.depth + 1
                FROM connections c, hop
                WHERE hop.depth < $maxJumps AND hop.noteID IN (c.noteIDFrom, c.noteIDTo)
            ),
            near(noteID, distance) AS (SELECT noteID, MIN(depth) FROM hop GROUP BY noteID)
            SELECT title, path, MIN(tier) AS bestTier, MIN(score) AS bestScore, near.distance
            FROM ({MATCH_TIERS}) matches JOIN near USING(noteID) GROUP BY noteID
            ORDER BY bestTier, near.distance, bestScore LIMIT $limit
            """, searchTerm, resultLimit, ("$anchorId", anchorId), ("$maxJumps", maxJumps));
    }

    private List<SearchResult> Matches(SqliteConnection db, string sql, string searchTerm, int resultLimit,
        params (string Name, object Value)[] extras)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$term", searchTerm);
        command.Parameters.AddWithValue("$pattern", $"%{searchTerm.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
        command.Parameters.AddWithValue("$phrase", $"\"{searchTerm.Replace("\"", "\"\"")}\"");
        command.Parameters.AddWithValue("$limit", resultLimit);
        foreach ((string name, object value) in extras) command.Parameters.AddWithValue(name, value);

        List<SearchResult> results = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new SearchResult(reader.GetString(0), reader.GetString(1), MATCH_NAMES[reader.GetInt32(2)], reader.GetInt32(4)));
        return results;
    }

    private SqliteConnection Open()
    {
        SqliteConnection db = new($"Data Source={dbPath}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static long LastId(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }
}
