using ARI.BrainVault;
using ARI.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Engram's second input: instead of a conversation, a job to reshape the brain itself (all of it, or named notes), with optional
/// instructions from the owner. It runs only while ARI is idle and has no time limit, so it reads every note in scope and asks the
/// model, with thinking on, which sentences belong to another subject. Code then moves those sentences word for word: the model
/// chooses where things go, but can never reword, drop or invent a fact. Afterwards it groups notes into theme hubs and links up
/// names mentioned in passing. Every step is its own git commit, so any of it can be undone.
/// </summary>
internal partial class Engram
{
    private const int REWORK_MAX_TOKENS   = 16000;
    private const int REWORK_MIN_WORDS    = 90;    // shorter notes are about one thing already
    private const int REWORK_MAX_PASSES   = 3;     // a note split out of another can itself be split, but not forever
    private const int ORGANISE_MAX_TOKENS = 16000;

    private static readonly string[] SubjectFolders = ["People", "Places", "Projects", "Events", "Topics", "Relationships", "Organisations"];
    private static readonly HashSet<string> NoteTypes = new(StringComparer.OrdinalIgnoreCase)
        { "person", "place", "project", "event", "topic", "facet", "organisation", "relationship", "thing", "idea" };

    private sealed class ReworkJob
    {
        public List<string>? Scope        { get; set; }   // null = the whole brain
        public string        Instructions { get; set; } = "";
        public DateTime      Created      { get; set; } = DateTime.UtcNow;
        public List<string>  Done         { get; set; } = new();   // notes already reworked, so a job paused by activity picks up where it stopped
        public bool          Organised    { get; set; }
        public bool          OrganiseOnly { get; set; }   // just the groups (queued after conversations): no note is split
    }

    private static readonly JsonSerializerOptions JobJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Queues a rework of the brain (or just the named notes) to run the next time ARI is idle. Returns the job's file name.</summary>
    internal static string QueueRework(IReadOnlyList<string>? scope, string? instructions, bool organiseOnly = false)
    {
        Directory.CreateDirectory(QueueDir);
        string file = Path.Combine(QueueDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.rework.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new ReworkJob { Scope = scope is { Count: > 0 } ? scope.ToList() : null, Instructions = instructions?.Trim() ?? "", OrganiseOnly = organiseOnly }, JobJson));
        Shared.Logger.LogInformation("[Engram] queued a brain rework ({Scope}).", scope is { Count: > 0 } ? $"{scope.Count} note(s)" : "whole brain");
        return Path.GetFileName(file);
    }

    /// <summary>Queued rework jobs, oldest first. Conversations always go first: these run only when no snapshot is waiting.</summary>
    internal static List<string> QueuedReworks() =>
        Directory.Exists(QueueDir) ? Directory.EnumerateFiles(QueueDir, "*.rework.json").Order(StringComparer.Ordinal).ToList() : new();

    /// <summary>Works through one rework job. Stops between notes when <paramref name="stillIdle"/> says ARI is busy again, saving its
    /// progress in the job file; returns true once the job is finished and removed.</summary>
    internal async Task<bool> RunRework(string file, Func<bool> stillIdle)
    {
        ReworkJob? job;
        try { job = JsonSerializer.Deserialize<ReworkJob>(File.ReadAllText(file), JobJson); }
        catch (Exception ex) { Shared.Logger.LogWarning("[Engram] unreadable rework job {File}: {Err} — dropped.", Path.GetFileName(file), ex.Message); DropSnapshot(file); return true; }
        if (job is null) { DropSnapshot(file); return true; }

        await engramLock.WaitAsync();
        try
        {
            Brain.Index();
            if (job.Done.Count == 0)
            {
                // The dev brain is a throwaway copy; backing it up would push the owner's real backups out of the rotation.
                if (!Shared.DevMode) Shared.Logger.LogInformation("[Engram] rework: {Backup}", BrainBackup.Backup());
                Shape("Rework: link names that are mentioned and give shared names notes of their own");
            }

            Queue<(string Name, int Pass)> todo = new(job.OrganiseOnly ? [] : Candidates(job).Select(n => (n, 1)));
            int moved = 0;
            while (todo.Count > 0)
            {
                if (!stillIdle()) { Save(file, job); Shared.Logger.LogInformation("[Engram] rework paused — ARI is busy; {Left} note(s) left.", todo.Count); return false; }
                (string name, int pass) = todo.Dequeue();
                if (Brain.GetNote(name) is not { } note) continue;
                if (ServerDownUntil(out TimeSpan wait)) { Save(file, job); await Task.Delay(wait); return false; }

                List<string>? grown = await ReworkNote(note, job.Instructions);
                NoteServerAnswer(grown is not null);
                if (grown is null) { Save(file, job); return false; }   // the model didn't answer: try this note again next time
                job.Done.Add(name);
                moved += grown.Count;
                // Anything this made or added to that is still long gets its own turn, a bounded number of times.
                if (pass < REWORK_MAX_PASSES)
                    foreach (string g in grown.Prepend(name).Distinct(StringComparer.OrdinalIgnoreCase))
                        if (Brain.GetNote(g) is { } gn && Words(gn.Content) > BrainShape.MaxWords && !todo.Any(t => t.Name.Equals(g, StringComparison.OrdinalIgnoreCase)))
                            todo.Enqueue((g, pass + 1));
                Save(file, job);
            }

            if (!job.Organised)
            {
                if (!stillIdle()) { Save(file, job); return false; }
                int hubs = await Curate(job.Instructions);
                if (hubs < 0) { Save(file, job); return false; }
                job.Organised = true;
                Save(file, job);
            }
            Shape("Rework: link the notes the rework made");
            Shared.Logger.LogInformation("[Engram] rework finished — {Notes} note(s) read, {Moved} move(s).", job.Done.Count, moved);
            DropSnapshot(file);
            return true;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] rework failed: {Err}", ex.Message);
            Save(file, job);
            return false;
        }
        finally { engramLock.Release(); }
    }

    private static void Save(string file, object job) { try { File.WriteAllText(file, JsonSerializer.Serialize(job, JobJson)); } catch { } }

    // Biggest first: they are the ones holding facts that belong elsewhere. Logs and hubs are records and indexes, not subjects.
    private static List<string> Candidates(ReworkJob job)
    {
        IEnumerable<Note> notes = job.Scope is { Count: > 0 } scope
            ? scope.Select(Brain.GetNote).OfType<Note>()
            : Brain.AllNotes().Where(n => !IsRecord(n) && Words(n.Content) >= REWORK_MIN_WORDS);
        return notes.Where(n => !job.Done.Contains(n.Name, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Content.Length).Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRecord(Note n) =>
        n.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.Type, "discussion", StringComparison.OrdinalIgnoreCase);

    private static int Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static void Shape(string message)
    {
        try
        {
            (int repointed, int created) = BrainShape.GrowPendingLinks();
            int linked = BrainShape.AutoLink();
            BrainShape.SyncGroups();
            Commit($"{message} ({repointed} link(s) fixed, {created} topic(s) made, {linked} note(s) linked)");
        }
        catch (Exception ex) { Shared.Logger.LogWarning("[Engram] shape pass failed: {Err}", ex.Message); }
    }

    private static void Commit(string message)
    {
        try { AriGit.Run(Brain.VaultRoot, ["add", "-A"]); AriGit.Commit(Brain.VaultRoot, message); }
        catch (Exception ex) { Shared.Logger.LogWarning("[Engram] rework commit failed: {Err}", ex.Message); }
    }

    // ── One note ─────────────────────────────────────────────────────────────────────

    // A block is one sentence of running text, one list item, one callout, or a heading (headings never move).
    private sealed record Block(int Id, int Paragraph, string Text, bool Heading);

    private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+(?=[A-Z0-9\[\(""'])", RegexOptions.Compiled);
    private static readonly Regex ListItem    = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Compiled);

    private static List<Block> SplitBlocks(string body)
    {
        List<Block> blocks = new();
        string[] paragraphs = Regex.Split(body.Replace("\r\n", "\n").Trim(), @"\n\s*\n");
        for (int p = 0; p < paragraphs.Length; p++)
        {
            string para = paragraphs[p].TrimEnd();
            if (para.Length == 0) continue;
            string[] lines = para.Split('\n');
            if (lines.All(l => l.TrimStart().StartsWith('>')) || para.TrimStart().StartsWith("```"))
            { blocks.Add(new Block(blocks.Count, p, para, false)); continue; }   // a callout or code block moves whole
            StringBuilder prose = new();
            void FlushProse()
            {
                if (prose.Length == 0) return;
                foreach (string s in SentenceEnd.Split(prose.ToString().Trim())) if (s.Trim().Length > 0) blocks.Add(new Block(blocks.Count, p, s.Trim(), false));
                prose.Clear();
            }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("**Related:**") || line.TrimStart().StartsWith("**Facets:**") || line.TrimStart().StartsWith("**Facet of:**") || line.TrimStart().StartsWith("**Parent:**"))
                { FlushProse(); blocks.Add(new Block(blocks.Count, p, line, true)); continue; }
                if (ListItem.IsMatch(line))
                {
                    FlushProse();
                    StringBuilder item = new(line);
                    while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && !ListItem.IsMatch(lines[i + 1].TrimStart() == lines[i + 1] ? lines[i + 1] : "x")) item.Append('\n').Append(lines[++i]);
                    blocks.Add(new Block(blocks.Count, p, item.ToString(), false));
                    continue;
                }
                prose.Append(line).Append('\n');
            }
            FlushProse();
        }
        return blocks;
    }

    private static string JoinBlocks(IEnumerable<Block> blocks)
    {
        StringBuilder sb = new();
        int? lastPara = null;
        bool lastWasLine = false;
        foreach (Block b in blocks)
        {
            bool line = b.Heading || ListItem.IsMatch(b.Text) || b.Text.TrimStart().StartsWith('>');
            if (lastPara is not null) sb.Append(b.Paragraph != lastPara || line || lastWasLine ? (b.Paragraph != lastPara ? "\n\n" : "\n") : " ");
            sb.Append(b.Text);
            lastPara = b.Paragraph; lastWasLine = line;
        }
        // A heading left with nothing under it goes.
        return Regex.Replace(sb.ToString(), @"(?m)^#{1,6} .*\n+(?=#{1,6} |\z)", "").Trim();
    }

    private sealed record Move(string Name, string Type, List<int> Blocks, List<string> Groups);
    private const int MAX_INTO_EXISTING = 6;   // more than this into one existing note is dumping, not filing: the rest stays put

    /// <summary>Asks where each part of one note belongs and moves it there. Returns the notes made or added to (empty when the note
    /// was already about one thing), or null when the model didn't answer.</summary>
    private async Task<List<string>?> ReworkNote(Note note, string instructions)
    {
        // Margin thoughts come out first and go back beside their fact, wherever that fact ends up.
        (string body, List<ThoughtRecord> thoughts) = Brain.TakeThoughts(note.Content);
        List<Block> blocks = SplitBlocks(body);
        if (blocks.Count(b => !b.Heading) < 3) return new();

        string numbered = string.Join("\n", blocks.Select(b => b.Heading ? $"   {b.Text}" : $"[{b.Id}] {b.Text}"));
        string others = string.Join("\n", Brain.AllNotes().Where(n => !n.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase) && n.Name != note.Name)
            .Select(n => $"- {n.Name}" + (n.Type is { Length: > 0 } t ? $" ({t}" : " (") + $", {Words(n.Content)} words)"));

        string system = ResolveTemplate("ReworkSystem", ReworkSystemDefault);
        string task = ResolveTemplate("ReworkTask", ReworkTaskDefault,
            ("note", note.Name), ("type", note.Type ?? "untyped"), ("title", note.Title), ("words", Words(note.Content).ToString()),
            ("blocks", numbered), ("notes", others.Length > 0 ? others : "(none yet)"),
            ("instructions", instructions.Length > 0 ? $"\nThe owner's instructions for this rework: {instructions}\n" : ""));

        string? answer = await CompleteThinking(system, task, REWORK_MAX_TOKENS);
        if (answer is null) return null;
        List<Move> moves = ParseMoves(answer, blocks, note);
        // "About one thing" is not an answer for a note several times the size limit: one subject that big still has sub-subjects.
        if (moves.Count == 0 && Words(note.Content) > 2 * BrainShape.MaxWords)
        {
            string push = task + $"\n\nThis note is {Words(note.Content)} words, far over the {BrainShape.MaxWords}-word limit. Even if it is about one " +
                "subject, split it by sub-subject into new notes of its own (each in this note's group), keeping only a short summary here. " +
                "An empty answer is not allowed.";
            answer = await CompleteThinking(system, push, REWORK_MAX_TOKENS);
            if (answer is not null) moves = ParseMoves(answer, blocks, note);
        }
        if (moves.Count == 0) { Shared.Logger.LogInformation("[Engram] rework: '{Note}' is about one thing — left as it is.", note.Name); return new(); }

        HashSet<int> taken = moves.SelectMany(m => m.Blocks).ToHashSet();
        List<string> grown = new();
        List<ThoughtRecord> unplaced = new(thoughts);
        string WithThoughts(string text)
        {
            foreach (ThoughtRecord t in unplaced.ToList())
                if (t.SpanText.Length > 0 && text.Contains(t.SpanText, StringComparison.Ordinal)) { text = Brain.PlaceThought(text, t); unplaced.Remove(t); }
            return text;
        }
        foreach (Move m in moves)
        {
            string text = WithThoughts(JoinBlocks(blocks.Where(b => m.Blocks.Contains(b.Id))));
            if (text.Length == 0) continue;
            if (Brain.GetNote(m.Name) is { } existing)
            {
                Brain.EditNote(existing.Name, existing.Content.TrimEnd() + "\n\n" + text, existing.Aliases);
                grown.Add(existing.Name);
            }
            else
            {
                // A new note says which groups it belongs to; with none given, it belongs with the note it came from.
                List<string> groups = m.Groups.Count > 0 ? m.Groups : new() { note.Title };
                Brain.AddNote(m.Name, BrainShape.WithGroups(text, groups), Array.Empty<string>(), m.Type);
                grown.Add(m.Name);
            }
        }

        // The source keeps what it didn't give away. Its groups line stays; member lists are rebuilt by code afterwards.
        string kept = JoinBlocks(blocks.Where(b => !taken.Contains(b.Id) && !(b.Heading && (b.Text.StartsWith("**Members:**") || b.Text.StartsWith("**Related:**") || b.Text.StartsWith("**Facets:**") || b.Text.StartsWith("**Facet of:**")))));
        string sourceBody = WithThoughts(kept);
        foreach (ThoughtRecord t in unplaced) sourceBody = Brain.PlaceThought(sourceBody, t);   // its fact is gone from everywhere: keep it, flagged for re-anchoring
        Brain.ReplaceBody(note.Name, sourceBody);
        BrainShape.SyncGroups();

        Commit($"Rework {note.Title}: moved {taken.Count} part(s) into {string.Join(", ", grown.Select(g => g.Contains('/') ? g[(g.LastIndexOf('/') + 1)..] : g))}");
        Shared.Logger.LogInformation("[Engram] rework: '{Note}' — {Parts} part(s) into {Count} note(s).", note.Name, taken.Count, grown.Count);
        return grown;
    }

    private static List<string> LinkLine(string content, string label)
    {
        Match m = Regex.Match(content, $@"(?m)^{Regex.Escape(label)}(.*)$");
        return m.Success ? Brain.GetWikilinks(m.Groups[1].Value) : new();
    }

    // The model's plan, checked: real block numbers only, each block in one place, headings never, the note never into itself or a log,
    // and only into the folders a subject belongs in. A note must keep at least one sentence unless it becomes a hub of facets.
    private static List<Move> ParseMoves(string answer, List<Block> blocks, Note source)
    {
        List<Move> moves = new();
        string? json = OuterJson(answer, '{', '}');
        if (json is null) return moves;
        HashSet<int> valid = blocks.Where(b => !b.Heading).Select(b => b.Id).ToHashSet(), used = new();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("notes", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return moves;
            foreach (JsonElement el in list.EnumerateArray())
            {
                string name = el.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "").Trim() : "";
                string type = el.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim().ToLowerInvariant() : "topic";
                if (!NoteTypes.Contains(type)) type = "topic";
                name = Placed(name, type);
                if (name.Length == 0 || name.Equals(source.Name, StringComparison.OrdinalIgnoreCase) || name.Equals(source.Title, StringComparison.OrdinalIgnoreCase)) continue;
                List<int> ids = el.TryGetProperty("blocks", out JsonElement b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).Where(i => valid.Contains(i) && used.Add(i)).ToList()
                    : new();
                if (Brain.GetNote(name) is not null && ids.Count > MAX_INTO_EXISTING) { foreach (int extra in ids.Skip(MAX_INTO_EXISTING)) used.Remove(extra); ids = ids.Take(MAX_INTO_EXISTING).ToList(); }
                if (ids.Count == 0) continue;
                List<string> groups = el.TryGetProperty("groups", out JsonElement g) && g.ValueKind == JsonValueKind.Array
                    ? g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => GroupName(x.GetString()!)).Where(x => x.Length > 0 && !x.Equals(name[(name.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList()
                    : new();
                Move? same = moves.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (same is not null) same.Blocks.AddRange(ids); else moves.Add(new Move(name, type, ids, groups));
            }
        }
        catch (JsonException) { return new(); }
        return moves;
    }

    // Where a new note goes: an existing note by its exact name or title, otherwise a subject folder (Topics when unsure). Never a log or hub.
    private static string Placed(string name, string type)
    {
        name = Regex.Replace(name.Replace("[[", "").Replace("]]", "").Replace(".md", ""), @"\s+", " ").Trim().Trim('/');
        if (name.Length == 0 || name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Themes", StringComparison.OrdinalIgnoreCase)) return "";
        if (Brain.GetNote(name) is { } existing) return existing.Name;
        string title = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        string folder = name.Contains('/') ? name[..name.IndexOf('/')] : "";
        if (title.Length < 2 || title.Length > 80) return "";
        if (!SubjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            folder = type switch { "person" or "facet" => "People", "place" => "Places", "project" => "Projects", "event" => "Events", "relationship" => "Relationships", "organisation" => "Organisations", _ => "Topics" };
        else folder = SubjectFolders.First(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return $"{folder}/{title}";
    }

    // A group is named by a note's title (an existing note, or a new hub): plain words, no folder, no brackets.
    private static readonly HashSet<string> FolderNames = new(StringComparer.OrdinalIgnoreCase)
        { "People", "Places", "Events", "Relationships", "Topics", "Projects", "Organisations", "Groups", "Themes", "Notes" };

    internal static string GroupCanon(string g) => Regex.Replace(g.ToLowerInvariant(), @"['’\s]", "");

    private static string GroupName(string raw)
    {
        string g = Regex.Replace(raw.Replace("[[", "").Replace("]]", ""), @"\s+", " ").Trim().Trim('/');
        if (g.Contains('/')) g = g[(g.LastIndexOf('/') + 1)..];
        if (g.Length < 2 || g.Length > 60 || g.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase)) return "";
        if (FolderNames.Contains(g)) return "";   // a folder says what kind of note it is; it is never a neighbourhood
        // a relationship note's title used as a group is that bond's bridge ("A and B Relationship" → "A and B")
        Match bond = Regex.Match(g, @"^(.+ and .+?) (Relationship|Friendship)$", RegexOptions.IgnoreCase);
        if (bond.Success) g = bond.Groups[1].Value;
        int amp = g.IndexOf(" & ", StringComparison.Ordinal);
        if (amp > 0) g = g[..amp].Trim();   // one theme per group: "Health & Body" is filed under its first theme
        // the same group under another spelling ("ARIs Persona" / "ARI's Persona") is that group
        string canon = GroupCanon(g);
        if (Brain.AllNotes().FirstOrDefault(n => GroupCanon(n.Title) == canon) is { } same) return same.Title;
        foreach (string known in BrainShape.KnownGroups()) if (GroupCanon(known) == canon) return known;
        return Brain.GetNote(g)?.Title ?? g;
    }

    private static string? OuterJson(string text, char open, char close)
    {
        int end = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (end >= 0) text = text[(end + 8)..];
        int a = text.IndexOf(open), b = text.LastIndexOf(close);
        return a >= 0 && b > a ? text[a..(b + 1)] : null;
    }

    // ── Theme hubs ───────────────────────────────────────────────────────────────────

    /// <summary>Gives every note the groups it belongs to: a note may sit in several, a group may sit in a group. Code writes the
    /// "**Groups:**" lines and the member lists. Returns notes changed, or -1 when the model didn't answer.</summary>
    private async Task<int> Organise(string instructions)
    {
        BrainShape.SyncGroups();
        List<Note> notes = Brain.AllNotes().Where(n => !n.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase)).ToList();
        if (notes.Count < 4) return 0;
        string listing = string.Join("\n", notes.Select(n =>
        {
            string text = Regex.Replace(Regex.Replace(n.Content, @"(?m)^\*\*(Groups|Members):\*\*.*$", ""), @"\s+", " ").Trim();
            List<string> gs = BrainShape.GroupsOf(n.Content);
            return $"- {n.Title} ({n.Type ?? "untyped"}{(gs.Count > 0 ? "; in " + string.Join(", ", gs) : "")}): {(text.Length > 120 ? text[..120] + "…" : text)}";
        }));
        string? answer = await CompleteThinking(ResolveTemplate("OrganiseSystem", OrganiseSystemDefault),
            ResolveTemplate("OrganiseTask", OrganiseTaskDefault, ("notes", listing),
                ("instructions", instructions.Length > 0 ? $"\nThe owner's instructions for this rework: {instructions}\n" : "")), ORGANISE_MAX_TOKENS);
        if (answer is null) return -1;
        string? json = OuterJson(answer, '{', '}');
        if (json is null) return 0;
        Dictionary<string, HashSet<string>> add = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array) return 0;
            foreach (JsonElement g in groups.EnumerateArray())
            {
                string name = g.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? GroupName(n.GetString() ?? "") : "";
                if (name.Length == 0 || !g.TryGetProperty("members", out JsonElement ms) || ms.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement m in ms.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.String && Brain.GetNote(m.GetString()!) is { } member && !member.Title.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && !member.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase))
                        (add.TryGetValue(member.Name, out HashSet<string>? set) ? set : add[member.Name] = new(StringComparer.OrdinalIgnoreCase)).Add(name);
            }
        }
        catch (JsonException) { return 0; }
        int changed = 0;
        foreach ((string member, HashSet<string> gs) in add)
        {
            if (Brain.GetNote(member) is not { } note) continue;
            List<string> now = BrainShape.GroupsOf(note.Content);
            List<string> next = now.Concat(gs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (next.Count == now.Count) continue;
            Brain.EditNote(note.Name, BrainShape.WithGroups(note.Content, next), note.Aliases);
            changed++;
        }
        int groupsWritten = BrainShape.SyncGroups();
        if (changed > 0) Commit($"Rework: put {changed} note(s) into their groups ({groupsWritten} group(s) updated)");
        Shared.Logger.LogInformation("[Engram] rework: grouped {N} note(s).", changed);
        return changed;
    }

    // ── The model ───────────────────────────────────────────────────────────────────

    // A direct completion with thinking on and a large budget: this runs while ARI is idle, so it may take its time to get it right.
    private async Task<string?> CompleteThinking(string system, string user, int maxTokens, bool think = true)
    {
        object body = new
        {
            model = "local",
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            stream = false, max_tokens = maxTokens, temperature = 0.4,
            chat_template_kwargs = new { enable_thinking = think },
        };
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement choice = doc.RootElement.GetProperty("choices")[0];
            string finish = choice.TryGetProperty("finish_reason", out JsonElement fr) ? fr.GetString() ?? "" : "";
            int used = doc.RootElement.TryGetProperty("usage", out JsonElement u) && u.TryGetProperty("completion_tokens", out JsonElement ct) ? ct.GetInt32() : 0;
            string content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            Shared.Logger.LogInformation("[Engram] rework call: finish={Finish}, {Used}/{Max} tokens, {Chars} answer chars.", finish, used, maxTokens, content.Length);
            return content;
        }
        catch (Exception ex) { Shared.Logger.LogWarning("[Engram] rework call failed: {Err}", ex.Message); return null; }
    }

    // ── Prompts (overridable in Agents.json under the Engram agent's PromptTemplates) ──────────────────────

    private const string ReworkSystemDefault =
        "You are Engram, the part of ARI that keeps her brain in order. The brain is a graph of notes joined by [[links]]. It should look like a " +
        "universe: many small notes, each about exactly one subject, gathered into neighbourhoods by the groups they belong to. One note holding " +
        "everything about many subjects is the opposite of that.\n\n" +
        "You are shown one note split into numbered parts (sentences, list items, callouts). Decide which parts are really about a different " +
        "subject and where each belongs. You never rewrite anything: the parts you choose are moved word for word, so choose whole parts.\n\n" +
        "Where parts go:\n" +
        "- A note keeps its own story. An event keeps what happened, a relationship keeps the story of the bond, a person keeps who they are. " +
        "Only parts about a DIFFERENT subject move. If the note is already about one thing, move nothing.\n" +
        "- A person's note keeps only who they are (name, age, where they live, what they are to the owner). Every other part about them goes " +
        "into an aspect note titled \"<Their name>'s <Aspect>\" in People/ (for example People/Sam's Work, People/Sam's Family, People/Sam's Hobbies, " +
        "People/Sam's Health, People/Sam's Plans, People/Sam's Preferences): a few broad aspects, never one per sentence, in group [[<Their name>]].\n" +
        "- The same for ARI herself and for a big project: \"ARI's Personality\", \"ARI's Communication Style\", in Topics/, group [[ARI]].\n" +
        "- A part about something that already has a note goes to that note only when it is squarely about it: use its exact name. A long note " +
        "(word counts are listed) takes at most a few parts; anything narrower starts its own note.\n" +
        "- A part about a subject with no note yet (a game, a tool, a hobby, a place, an organisation, an event, an idea) starts a new note named " +
        "the way a person would say it, in People/, Places/, Projects/, Events/, Topics/, Relationships/ or Organisations/.\n" +
        "- Every new note lists the groups it belongs to: the notes or themes it is part of, as many as truly fit (an aspect of a person is in " +
        "that person's group; a friend can be in \"<Owner>'s Friends\" and in a hobby's group). Groups are named by a note's title or a short theme name.\n" +
        "- Group parts: a new note should get every part about its subject, ideally three or more. No note for a single passing mention.\n" +
        "- A private part (a callout starting > [!sensitive]) may move only to a note about the same person.\n\n" +
        "Answer with JSON only, after thinking: {\"notes\":[{\"name\":\"Folder/Title\",\"type\":\"person|place|project|event|topic|facet|organisation|relationship\",\"groups\":[\"Group\", ...],\"blocks\":[part numbers]}]}. " +
        "Use {\"notes\":[]} when nothing should move.";

    private const string ReworkTaskDefault =
        "Note: {note} (type: {type}, {words} words)\n{instructions}\nIts parts:\n{blocks}\n\nOther notes in the brain:\n{notes}\n\n" +
        "Which parts belong somewhere else, and where?";

    private const string OrganiseSystemDefault =
        "You are Engram, the part of ARI that keeps her brain in order. Every note belongs to one or more groups, and groups are the brain's " +
        "neighbourhoods: a note sits near the groups it belongs to, and a note in two groups draws those groups together. A group is either a " +
        "note that exists (a person, a project, ARI) or a theme with a short plain name (\"Sam's Friends\" style: whose, and what). Groups can " +
        "belong to groups (a person's friends group belongs to that person).\n\n" +
        "- Give every note that has no group at least one; add a second or third only when it truly belongs there.\n" +
        "- Prefer groups that already exist. Make a new theme only when four or more notes share it.\n" +
        "- Use the note titles exactly as listed. Never put a day log in a group.\n\n" +
        "Answer with JSON only, after thinking: {\"groups\":[{\"name\":\"Group\",\"members\":[\"Note title\", ...]}]} listing only the memberships to ADD.";

    private const string OrganiseTaskDefault = "The notes (with the groups they are in now):\n{notes}\n{instructions}\nWhich groups should each note join?";
}
