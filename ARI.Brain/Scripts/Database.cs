using Microsoft.Data.Sqlite;
using System.Text;

namespace ARI.Brain;

// Derived and disposable — the markdown files are the source of truth; delete this and rebuild anytime.
// Every call opens a fresh connection: nothing stays resident, the OS page cache provides the speed.
// Every SQL statement in the brain lives in this file. BrainModule and Note call named methods here;
// they never write SQL themselves.
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
            type     TEXT,
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
            kind         TEXT NOT NULL,
            spanText     TEXT NOT NULL,
            comment      TEXT NOT NULL,
            confidence   TEXT NOT NULL,
            created      TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE note_search USING fts5(title, content, content='notes', content_rowid='noteID');
        """;

    // Tier weight per term match: title/alias hits dominate, a bare content mention barely
    // registers alone but adds up across several terms.
    private const double TIER_TITLE          = 100.0;
    private const double TIER_ALIAS          = 90.0;
    private const double TIER_TITLE_PARTIAL  = 50.0;
    private const double TIER_ALIAS_PARTIAL  = 40.0;
    private const double TIER_TITLE_IN_TERM  = 45.0;
    private const double TIER_ALIAS_IN_TERM  = 35.0;
    private const double TIER_CONTENT        = 10.0;

    internal static string Path { get; set; } = string.Empty;

    // ── Rebuild ──────────────────────────────────────────────────────────────────────

    internal static IndexStats Rebuild(List<(string Path, Note.Parsed Parsed, DateTime Updated)> files)
    {
        List<string> dirtyBefore = File.Exists(Path) && new FileInfo(Path).Length > 0 ? DirtyTitles() : new List<string>();

        using SqliteConnection db = Open();
        Run(db, "PRAGMA foreign_keys = ON;");
        using SqliteTransaction transaction = db.BeginTransaction();
        Run(db, SCHEMA);

        Dictionary<string, long> idsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, Note.Parsed parsed, DateTime updated) in files)
        {
            string title = System.IO.Path.GetFileNameWithoutExtension(path);
            Run(db, "INSERT INTO notes(title, path, content, type, updated) VALUES ($title, $path, $content, $type, $updated)",
                ("$title", title), ("$path", path), ("$content", parsed.Body),
                ("$type", (object?)parsed.Type ?? DBNull.Value),
                ("$updated", updated.ToString("yyyy-MM-ddTHH:mm:ssZ")));
            idsByName[title] = LastId(db);
        }

        int aliasCount = 0;
        List<string> skippedAliases = new();
        foreach ((string path, Note.Parsed parsed, DateTime _) in files)
        {
            string title = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (string alias in parsed.AliasList)
            {
                if (idsByName.ContainsKey(alias))
                {
                    skippedAliases.Add($"{title}: alias '{alias}' shadows an existing name");
                    continue;
                }
                Run(db, "INSERT INTO aliases(alias, noteID) VALUES ($alias, $id)", ("$alias", alias), ("$id", idsByName[title]));
                idsByName[alias] = idsByName[title];
                aliasCount++;
            }
        }

        int edgeCount = 0;
        List<string> unresolved = new();
        foreach ((string path, Note.Parsed parsed, DateTime _) in files)
        {
            long source = idsByName[System.IO.Path.GetFileNameWithoutExtension(path)];
            foreach (string target in BrainModule.GetWikilinks(parsed.Body))
            {
                if (!idsByName.TryGetValue(target, out long destination))
                {
                    unresolved.Add($"{System.IO.Path.GetFileNameWithoutExtension(path)} -> {target}");
                    continue;
                }
                if (destination == source) continue;
                Run(db, "INSERT OR IGNORE INTO connections(noteIDFrom, noteIDTo) VALUES ($from, $to)",
                    ("$from", source), ("$to", destination));
                edgeCount++;
            }
        }

        Run(db, "INSERT INTO note_search(rowid, title, content) SELECT noteID, title, content FROM notes");
        foreach (string title in dirtyBefore)
            Run(db, "UPDATE notes SET dirty = 1 WHERE title = $title", ("$title", title));

        int thoughtCount = 0;
        foreach ((string path, Note.Parsed parsed, DateTime _) in files)
        {
            long noteId = idsByName[System.IO.Path.GetFileNameWithoutExtension(path)];
            foreach (Note.ParsedThought thought in Note.ParseThoughts(parsed.Body))
            {
                Run(db, "INSERT INTO annotations(noteID, kind, spanText, comment, confidence, created) VALUES ($id, $kind, $span, $comment, $confidence, $created)",
                    ("$id", noteId), ("$kind", thought.Kind), ("$span", thought.SpanText),
                    ("$comment", thought.Comment), ("$confidence", thought.Confidence), ("$created", thought.Created));
                thoughtCount++;
            }
        }
        transaction.Commit();

        return new IndexStats(files.Count, edgeCount, aliasCount, thoughtCount, unresolved, skippedAliases);
    }

    // ── Notes ────────────────────────────────────────────────────────────────────────

    internal static Note? FindNote(string name) => QueryNotes("""
        SELECT noteID, title, path FROM notes WHERE title = $name
        UNION SELECT n.noteID, n.title, n.path FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $name
        UNION SELECT noteID, title, path FROM notes WHERE substr(path, 1, length(path) - 3) = $name
        LIMIT 1
        """, ("$name", name)).FirstOrDefault();

    internal static Note NoteById(long id) =>
        QueryNotes("SELECT noteID, title, path FROM notes WHERE noteID = $id", ("$id", id)).First();

    internal static List<Note> AllNotes() => QueryNotes("SELECT noteID, title, path FROM notes");

    internal static List<string> AllTitles() => Column("SELECT title FROM notes ORDER BY title");

    internal static List<string> AllPaths() => Column("SELECT substr(path, 1, length(path) - 3) FROM notes ORDER BY path");

    internal static List<string> TitlesInFolder(string folderPath) =>
        Column("SELECT title FROM notes WHERE path LIKE $inside AND path NOT LIKE $deeper ORDER BY title",
            ("$inside", folderPath.Length > 0 ? $"{folderPath}/%" : "%"),
            ("$deeper", folderPath.Length > 0 ? $"{folderPath}/%/%" : "%/%"));

    internal static List<(string Title, string Alias)> AliasPairs()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT n.title, a.alias FROM aliases a JOIN notes n USING(noteID) ORDER BY n.title";
        List<(string, string)> pairs = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) pairs.Add((reader.GetString(0), reader.GetString(1)));
        return pairs;
    }

    internal static List<Note> LinksFrom(long noteId) => QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE noteID IN (SELECT noteIDTo FROM connections WHERE noteIDFrom = $id)", ("$id", noteId));

    internal static List<Note> LinksTo(long noteId) => QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE noteID IN (SELECT noteIDFrom FROM connections WHERE noteIDTo = $id)", ("$id", noteId));

    internal static List<Note> ChildrenOf(string name) => QueryNotes(
        "SELECT noteID, title, path FROM notes WHERE path LIKE $inside AND path NOT LIKE $deeper",
        ("$inside", $"{name}/%"), ("$deeper", $"{name}/%/%"));

    internal static List<Note> UnknownStubs() => QueryNotes("SELECT noteID, title, path FROM notes WHERE path LIKE 'Unknown/%'");

    internal static Note? AliasOwner(string title, long excludeId) => QueryNotes(
        "SELECT n.noteID, n.title, n.path FROM aliases a JOIN notes n USING(noteID) WHERE a.alias = $title AND n.noteID != $id",
        ("$title", title), ("$id", excludeId)).FirstOrDefault();

    // ── Dirty set ────────────────────────────────────────────────────────────────────

    internal static void MarkDirty(IEnumerable<string> titles)
    {
        using SqliteConnection db = Open();
        foreach (string title in titles)
            Run(db, "UPDATE notes SET dirty = 1 WHERE title = $title OR noteID = (SELECT noteID FROM aliases WHERE alias = $title)",
                ("$title", title));
    }

    internal static List<string> DirtyTitles() => Column("SELECT title FROM notes WHERE dirty = 1 ORDER BY title");

    internal static void ClearDirty(IEnumerable<string> titles)
    {
        using SqliteConnection db = Open();
        foreach (string title in titles)
            Run(db, "UPDATE notes SET dirty = 0 WHERE title = $title", ("$title", title));
    }

    // ── Thoughts ─────────────────────────────────────────────────────────────────────

    internal static List<ThoughtRecord> ThoughtsForNote(long noteId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT kind, spanText, comment, confidence, created FROM annotations WHERE noteID = $id ORDER BY created";
        command.Parameters.AddWithValue("$id", noteId);
        List<ThoughtRecord> results = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ThoughtRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return results;
    }

    internal static List<(string NoteTitle, ThoughtRecord Thought)> RecentThoughts(int limit)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            SELECT n.title, a.kind, a.spanText, a.comment, a.confidence, a.created
            FROM annotations a JOIN notes n USING(noteID)
            ORDER BY a.created DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        List<(string, ThoughtRecord)> results = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), new ThoughtRecord(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))));
        return results;
    }

    // ── Search ───────────────────────────────────────────────────────────────────────

    internal static List<SearchResult> Search(IReadOnlyList<string> terms, int resultLimit)
    {
        string matches = MatchesFor(terms, out (string Name, object Value)[] parameters);
        return Ranked($"WITH RECURSIVE {matches}", "hits", parameters, resultLimit);
    }

    internal static List<SearchResult> SearchNear(long anchorId, IReadOnlyList<string> terms, int maxJumps, int resultLimit)
    {
        string matches = MatchesFor(terms, out (string Name, object Value)[] parameters);
        string query = $"""
            WITH RECURSIVE hop(noteID, depth) AS (
                SELECT $anchorId, 0
                UNION
                SELECT CASE WHEN c.noteIDFrom = hop.noteID THEN c.noteIDTo ELSE c.noteIDFrom END, hop.depth + 1
                FROM connections c, hop
                WHERE hop.depth < $maxJumps AND hop.noteID IN (c.noteIDFrom, c.noteIDTo)
            ),
            near(noteID) AS (SELECT DISTINCT noteID FROM hop),
            {matches},
            nearHits(term, noteID, tier) AS (SELECT h.term, h.noteID, h.tier FROM hits h JOIN near USING(noteID))
            """;
        parameters = parameters.Append(("$anchorId", anchorId)).Append(("$maxJumps", (object)maxJumps)).ToArray();
        return Ranked(query, "nearHits", parameters, resultLimit);
    }

    internal static Dictionary<long, (int Depth, long? Predecessor)> Reachability(long seedId, int maxJumps)
    {
        using SqliteConnection db = Open();
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
        command.Parameters.AddWithValue("$seedId", seedId);
        command.Parameters.AddWithValue("$maxJumps", maxJumps);

        Dictionary<long, (int, long?)> result = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetInt64(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        return result;
    }

    // ── Graph walk (seed ranking + adjacency skeleton) ─────────────────────────────────

    // Seeds for a walk: the highest total-degree (inbound + outbound) notes — where sprawl lives.
    internal static List<Note> SeedsByDegree(int limit) => QueryNotes("""
        SELECT n.noteID, n.title, n.path,
               (SELECT COUNT(*) FROM connections c WHERE c.noteIDFrom = n.noteID OR c.noteIDTo = n.noteID) AS degree
        FROM notes n
        ORDER BY degree DESC, n.title
        LIMIT $limit
        """, ("$limit", limit));

    // Adjacency skeleton for the neighbourhood BFS-reachable from a seed within maxJumps, capped at
    // `cap` nodes (nearest first). One block per node: full path + [type], then its inbound (<) and
    // outbound (>) connections by bare title. Keeps context minimal while showing every node's wiring
    // so the agent can spot edges that don't belong.
    internal static string Skeleton(long seedId, int maxJumps, int cap)
    {
        List<long> ids = Reachability(seedId, maxJumps)
            .OrderBy(kv => kv.Key == seedId ? -1 : kv.Value.Depth)
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Key)
            .Take(cap)
            .ToList();
        if (ids.Count == 0) return string.Empty;

        string idList = string.Join(",", ids);
        using SqliteConnection db = Open();

        // node meta: id -> (path-without-.md, type)
        Dictionary<long, (string Path, string? Type)> meta = new();
        using (SqliteCommand cmd = db.CreateCommand())
        {
            // Real on-disk path (WITH .md) so read_file/edit_file/find_files get valid filenames straight from the skeleton.
            cmd.CommandText = $"SELECT noteID, path, type FROM notes WHERE noteID IN ({idList})";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                meta[r.GetInt64(0)] = (r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }

        // every edge touching a neighbourhood node (targets may sit outside the neighbourhood — still shown)
        Dictionary<long, List<string>> outbound = new(), inbound = new();
        using (SqliteCommand cmd = db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT c.noteIDFrom, f.title, c.noteIDTo, t.title
                FROM connections c
                JOIN notes f ON f.noteID = c.noteIDFrom
                JOIN notes t ON t.noteID = c.noteIDTo
                WHERE c.noteIDFrom IN ({idList}) OR c.noteIDTo IN ({idList})
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                long from = r.GetInt64(0), to = r.GetInt64(2);
                string fromTitle = r.GetString(1), toTitle = r.GetString(3);
                if (meta.ContainsKey(from)) (outbound.TryGetValue(from, out List<string>? o) ? o : outbound[from] = new()).Add(toTitle);
                if (meta.ContainsKey(to))   (inbound.TryGetValue(to, out List<string>? i)  ? i : inbound[to]  = new()).Add(fromTitle);
            }
        }

        StringBuilder sb = new();
        foreach (long id in ids)
        {
            (string path, string? type) = meta[id];
            sb.Append(path);
            if (!string.IsNullOrEmpty(type)) sb.Append("  [").Append(type).Append(']');
            sb.Append('\n');
            if (inbound.TryGetValue(id, out List<string>? ins))
                foreach (string t in ins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) sb.Append("   < ").Append(t).Append('\n');
            if (outbound.TryGetValue(id, out List<string>? outs))
                foreach (string t in outs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) sb.Append("   > ").Append(t).Append('\n');
        }
        return sb.ToString();
    }

    // Terms become rows in a joined table, not a C# loop — one round trip regardless of term count.
    private static string MatchesFor(IReadOnlyList<string> terms, out (string Name, object Value)[] parameters)
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
                SELECT t.term, n.noteID, 5 FROM notes n, termTable t WHERE length(n.title) >= 4 AND instr(lower(t.term), lower(n.title)) > 0
                UNION ALL
                SELECT t.term, n.noteID, 6 FROM aliases a JOIN notes n USING(noteID), termTable t WHERE length(a.alias) >= 4 AND instr(lower(t.term), lower(a.alias)) > 0
                UNION ALL
                SELECT t.term, ns.rowid, 7 FROM note_search ns JOIN termTable t ON note_search MATCH t.phrase
            )
            """;
    }

    // "WITH RECURSIVE" unconditionally — SQLite allows it even when nothing in the chain recurses,
    // so one code path serves both Search and SearchNear.
    private static List<SearchResult> Ranked(string query, string hitsTable, (string Name, object Value)[] parameters, int resultLimit)
    {
        string sql = $"""
            {query},
            bestPerTerm(term, noteID, tier) AS (SELECT term, noteID, MIN(tier) FROM {hitsTable} GROUP BY term, noteID)
            SELECT n.noteID, n.title, n.path,
                   SUM(CASE bestPerTerm.tier
                       WHEN 1 THEN {TIER_TITLE} WHEN 2 THEN {TIER_ALIAS}
                       WHEN 3 THEN {TIER_TITLE_PARTIAL} WHEN 4 THEN {TIER_ALIAS_PARTIAL}
                       WHEN 5 THEN {TIER_TITLE_IN_TERM} WHEN 6 THEN {TIER_ALIAS_IN_TERM}
                       ELSE {TIER_CONTENT} END) AS score,
                   COUNT(DISTINCT bestPerTerm.term) AS termsMatched
            FROM bestPerTerm JOIN notes n USING(noteID)
            GROUP BY n.noteID
            ORDER BY score DESC, termsMatched DESC
            LIMIT $limit
            """;
        return QueryScored(sql, parameters.Append(("$limit", resultLimit)).ToArray());
    }

    // ── Generic execution ────────────────────────────────────────────────────────────

    private static SqliteConnection Open()
    {
        SqliteConnection db = new($"Data Source={Path}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = Command(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static List<string> Column(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        List<string> results = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    // First three columns must be noteID, title, path.
    private static List<Note> QueryNotes(string sql, params (string Name, object Value)[] parameters)
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

    // Query must be shaped (noteID, title, path, score, termsMatched).
    private static List<SearchResult> QueryScored(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = Command(db, sql, parameters);
        List<(long Id, string Title, string NotePath, double Score, int TermsMatched)> rows = new();
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt32(4)));

        List<SearchResult> results = new();
        foreach ((long id, string title, string notePath, double score, int termsMatched) in rows)
            results.Add(new SearchResult(
                new Note(id, title, notePath, Column("SELECT alias FROM aliases WHERE noteID = $id", ("$id", id))), score, termsMatched));
        return results;
    }

    private static long LastId(SqliteConnection db)
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
