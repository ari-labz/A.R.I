using Microsoft.Data.Sqlite;

namespace ARI.Brain;

public record IndexStats(int Notes, int Edges, int Aliases, IReadOnlyList<string> UnresolvedLinks, IReadOnlyList<string> SkippedAliases);

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

        using SqliteConnection db = new($"Data Source={dbPath}");
        db.Open();
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
