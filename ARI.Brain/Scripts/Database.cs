using Microsoft.Data.Sqlite;

namespace ARI.Brain;

/// <summary>
/// The index database: pointers, graph edges, aliases, and full-text search over the vault.
/// Derived and disposable — the markdown files are the source of truth; delete this and rebuild anytime.
/// Every call opens a fresh connection: nothing stays resident, the OS page cache provides the speed.
/// </summary>
internal static class Database
{
    internal const string SCHEMA = """
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

    internal static string Path { get; set; } = string.Empty;

    internal static SqliteConnection Open()
    {
        SqliteConnection db = new($"Data Source={Path}");
        db.Open();
        return db;
    }

    internal static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = Command(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>Single-column string query.</summary>
    internal static List<string> Column(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        List<string> results = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    /// <summary>Materializes Note handles from any query whose first three columns are noteID, title, path.</summary>
    internal static List<Note> QueryNotes(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        List<(long Id, string Title, string NotePath)> rows = new();
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));

        List<Note> notes = new();
        foreach ((long id, string title, string notePath) in rows)
            notes.Add(new Note(id, title, notePath, Column("SELECT alias FROM aliases WHERE noteID = $id", ("$id", id))));
        return notes;
    }

    /// <summary>Materializes ranked Recall candidates from a query shaped (noteID, title, path, score, termsMatched).</summary>
    internal static List<(Note Note, double Score, int TermsMatched)> QueryScored(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        List<(long Id, string Title, string NotePath, double Score, int TermsMatched)> rows = new();
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt32(4)));

        List<(Note, double, int)> results = new();
        foreach ((long id, string title, string notePath, double score, int termsMatched) in rows)
            results.Add((new Note(id, title, notePath, Column("SELECT alias FROM aliases WHERE noteID = $id", ("$id", id))), score, termsMatched));
        return results;
    }

    internal static long LastId(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, (string Name, object Value)[] parameters)
    {
        SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
