using ARI.BrainVault;
using ARI.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Engram's curator: shapes the brain's groups so the Brain page tells the owner's story. Engram never sees the page, so code measures
/// the brain the way the page will draw it (a note sits in its first group; a big group becomes a black hole, a mid-size one a spiral
/// galaxy, a group in two groups a bridge between them) and gives Engram that report. Engram answers with a whole plan: which groups
/// each note is in, which groups sit inside which, and which notes are the same thing under two names. Code applies it, measures again,
/// and Engram gets another look, until the shape is right or it has nothing more to change. It can take notes out of groups as well as
/// put them in, so the shape is corrected as new and contradicting information arrives, not only added to.
/// </summary>
internal partial class Engram
{
    private const int CURATE_PASSES     = 3;
    private const int CURATE_MAX_TOKENS = 12000;
    private const int CURATE_SNIPPET    = 50;    // characters of each note shown: enough to tell what it is, small enough for a big brain

    private sealed record Hood(string Name, int Size, string Kind, List<string> Parents);

    /// <summary>Plans and applies the brain's groups, up to a few passes. Returns notes changed, or -1 when the model didn't answer.</summary>
    private async Task<int> Curate(string instructions)
    {
        int changedAll = 0;
        for (int pass = 1; pass <= CURATE_PASSES; pass++)
        {
            BrainShape.SyncGroups();
            List<Note> notes = Brain.AllNotes().Where(n => !IsLogNote(n)).ToList();
            if (notes.Count < 4) return changedAll;
            (string report, bool good) = ShapeReport(notes);
            if (good && pass > 1) break;

            string listing = string.Join("\n", notes.Select(n =>
            {
                string text = Regex.Replace(Regex.Replace(n.Content, @"(?m)^\*\*(Groups|Members):\*\*.*$", ""), @"\s+", " ").Trim();
                List<string> gs = BrainShape.GroupsOf(n.Content).Select(GroupTitle).ToList();
                return $"- {n.Title} ({n.Type ?? "untyped"}{(gs.Count > 0 ? "; in " + string.Join(", ", gs) : "")}): {(text.Length > CURATE_SNIPPET ? text[..CURATE_SNIPPET] + "…" : text)}";
            }));
            string? answer = await CompleteThinking(ResolveTemplate("CurateSystem", CurateSystemDefault),
                ResolveTemplate("CurateTask", CurateTaskDefault, ("report", report), ("notes", listing), ("pass", pass.ToString()),
                    ("instructions", instructions.Length > 0 ? $"\nThe owner's instructions: {instructions}\n" : "")), CURATE_MAX_TOKENS);
            if (answer is null) return pass == 1 ? -1 : changedAll;
            int changed = ApplyPlan(OuterJson(answer, '{', '}'));
            Shared.Logger.LogInformation("[Engram] curate pass {Pass}: {N} change(s).", pass, changed);
            if (changed == 0) break;
            changedAll += changed;
        }
        int written = BrainShape.SyncGroups();
        if (changedAll > 0) Commit($"Curate: reshape the brain's groups ({changedAll} change(s), {written} group(s) updated)");
        (string final, _) = ShapeReport(Brain.AllNotes().Where(n => !IsLogNote(n)).ToList());
        Shared.Logger.LogInformation("[Engram] curate done:\n{Report}", final);
        return changedAll;
    }

    private static bool IsLogNote(Note n) =>
        n.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase) || n.Type == "discussion";

    private static string GroupTitle(string g) => g.Contains('/') ? g[(g.LastIndexOf('/') + 1)..] : g;

    // ── What the page will show ─────────────────────────────────────────────────────────────

    /// <summary>The brain measured the way the Brain page draws it, in words Engram can act on, and whether it already meets the rules.</summary>
    internal static (string Report, bool Good) ShapeReport(List<Note> notes)
    {
        int all = Brain.AllNotes().Count();
        int dense = Math.Max(20, Math.Min(150, (int)Math.Round(all * .08))), spiral = Math.Max(6, (int)Math.Round(dense * .3));
        Dictionary<string, Note> byTitle = new(StringComparer.OrdinalIgnoreCase);
        foreach (Note n in Brain.AllNotes()) byTitle.TryAdd(n.Title, n);

        Dictionary<string, int> size = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> parents = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> groupNames = new(StringComparer.OrdinalIgnoreCase);
        List<string> loose = new();
        foreach (Note n in notes)
        {
            List<string> gs = BrainShape.GroupsOf(n.Content).Select(GroupTitle).ToList();
            foreach (string g in gs) groupNames.Add(g);
            if (gs.Count == 0) { if (!n.Content.Contains("**Members:**")) loose.Add(n.Title); continue; }
            size[gs[0]] = size.GetValueOrDefault(gs[0]) + 1;
        }
        foreach (string g in groupNames)
            if (byTitle.TryGetValue(g, out Note? gn)) parents[g] = BrainShape.GroupsOf(gn.Content).Select(GroupTitle).ToList();

        List<Hood> hoods = groupNames.Select(g => new Hood(g, size.GetValueOrDefault(g) + 1,
                size.GetValueOrDefault(g) + 1 >= dense ? "black hole" : size.GetValueOrDefault(g) + 1 >= spiral ? "galaxy" : size.GetValueOrDefault(g) + 1 >= 3 ? "small cluster" : "too small to show",
                parents.GetValueOrDefault(g) ?? new()))
            .OrderByDescending(h => h.Size).ToList();

        int holes = hoods.Count(h => h.Kind == "black hole"), galaxies = hoods.Count(h => h.Kind == "galaxy");
        Hood? biggest = hoods.FirstOrDefault();
        double share = biggest is null ? 0 : (double)biggest.Size / Math.Max(1, notes.Count);
        List<Hood> tiny = hoods.Where(h => h.Kind == "too small to show").ToList();
        List<Hood> bridges = hoods.Where(h => h.Parents.Count >= 2).ToList();
        List<string> peopleNotGroups = notes.Where(n => n.Type == "person" && !groupNames.Contains(n.Title))
            .Where(n => notes.Count(m => m.Content.Contains($"[[{n.Title}]]", StringComparison.OrdinalIgnoreCase)) >= 4).Select(n => n.Title).ToList();

        List<string> problems = new();
        if (loose.Count > 0) problems.Add($"{loose.Count} note(s) are in no group, so they drift as loose stars: {string.Join(", ", loose.Take(25))}{(loose.Count > 25 ? ", …" : "")}");
        if (share > .3) problems.Add($"'{biggest!.Name}' holds {share:P0} of the brain: it swallows the picture. Split it into sub-groups that sit inside it.");
        if (holes == 0 && notes.Count >= 60) problems.Add("There is no black hole: no group is big enough to anchor the picture. Gather the main subjects into a few big groups.");
        if (holes > Math.Max(6, notes.Count / 150)) problems.Add($"{holes} black holes is too many: merge related big groups under shared parents.");
        if (tiny.Count > 0) problems.Add($"{tiny.Count} group(s) are too small to show (under 3 notes): {string.Join(", ", tiny.Take(20).Select(h => h.Name))}. Fold them into a bigger group, or put more notes in them.");
        if (peopleNotGroups.Count > 0) problems.Add($"These people are linked from many notes but are not groups, so their notes are scattered: {string.Join(", ", peopleNotGroups)}. Make each the group of the notes about them.");

        StringBuilder sb = new();
        sb.AppendLine($"The brain has {notes.Count} notes (plus day logs). On the Brain page a note sits in its FIRST group. A group of {dense}+ notes is drawn as a black hole, {spiral}–{dense - 1} as a spiral galaxy, 3–{spiral - 1} as a small cluster; under 3 is not shown as a group at all.");
        sb.AppendLine($"Now: {holes} black hole(s), {galaxies} galaxy(ies), {hoods.Count(h => h.Kind == "small cluster")} small cluster(s), {loose.Count} loose note(s), {bridges.Count} bridge group(s) (a group inside two groups).");
        sb.AppendLine("Groups, biggest first (size counts notes whose first group it is, plus the group's own note):");
        foreach (Hood h in hoods.Take(60))
            sb.AppendLine($"- {h.Name}: {h.Size} ({h.Kind}){(h.Parents.Count > 0 ? " — inside " + string.Join(" and ", h.Parents) : "")}");
        if (hoods.Count > 60) sb.AppendLine($"- … and {hoods.Count - 60} more");
        sb.AppendLine(problems.Count == 0 ? "No problems found: change only what is clearly wrong." : "Problems to fix:\n" + string.Join("\n", problems.Select(p => "- " + p)));
        return (sb.ToString().TrimEnd(), problems.Count == 0);
    }

    // ── Applying Engram's plan ────────────────────────────────────────────────────────────

    /// <summary>Applies a plan: merges first, then each listed note gets exactly the groups it is listed under (first listed = where it
    /// sits), and each listed group gets its parents. Notes the plan doesn't mention keep their groups. Returns the number of changes.</summary>
    private static int ApplyPlan(string? json)
    {
        if (json is null) return 0;
        int changed = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("merge", out JsonElement merges) && merges.ValueKind == JsonValueKind.Array)
                foreach (JsonElement m in merges.EnumerateArray())
                {
                    string from = m.TryGetProperty("from", out JsonElement f) ? f.GetString() ?? "" : "", into = m.TryGetProperty("into", out JsonElement t) ? t.GetString() ?? "" : "";
                    if (MergeNotes(from, into)) changed++;
                }

            if (!root.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array) return changed;
            // note → its groups in plan order; group → its parents
            Dictionary<string, List<string>> memberOf = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> parentsOf = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement g in groups.EnumerateArray())
            {
                string name = g.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? GroupName(n.GetString() ?? "") : "";
                if (name.Length == 0) continue;
                if (g.TryGetProperty("parents", out JsonElement ps) && ps.ValueKind == JsonValueKind.Array)
                    parentsOf[name] = ps.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => GroupName(p.GetString()!))
                        .Where(p => p.Length > 0 && !p.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!g.TryGetProperty("members", out JsonElement ms) || ms.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement m in ms.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.String || Brain.GetNote(m.GetString()!) is not { } member || IsLogNote(member)) continue;
                    if (member.Title.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    (memberOf.TryGetValue(member.Name, out List<string>? l) ? l : memberOf[member.Name] = new()).Add(name);
                }
            }
            foreach ((string note, List<string> gs) in memberOf)
                changed += SetGroups(note, gs.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            foreach ((string group, List<string> ps) in parentsOf)
            {
                Note? gn = Brain.GetNote(group);
                if (gn is null)
                {
                    if (ps.Count == 0) continue;
                    BrainShape.SyncGroups();   // a brand-new group gets its note first, then its parents
                    gn = Brain.GetNote(group);
                    if (gn is null) continue;
                }
                changed += SetGroups(gn.Name, ps);
            }
        }
        catch (JsonException) { }
        return changed;
    }

    private static int SetGroups(string noteName, List<string> groups)
    {
        if (Brain.GetNote(noteName) is not { } note) return 0;
        List<string> now = BrainShape.GroupsOf(note.Content).Select(GroupTitle).ToList();
        if (now.SequenceEqual(groups, StringComparer.OrdinalIgnoreCase)) return 0;
        Brain.EditNote(note.Name, BrainShape.WithGroups(note.Content, groups), note.Aliases);
        return 1;
    }

    /// <summary>Folds one note into another that is the same thing under another name: its text is added, every link to it points at
    /// the other note, and it goes. Never touches day logs, and never merges a note into itself.</summary>
    private static bool MergeNotes(string from, string into)
    {
        if (Brain.GetNote(from) is not { } a || Brain.GetNote(into) is not { } b || a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsLogNote(a) || IsLogNote(b)) return false;
        string text = Regex.Replace(a.Content, @"(?m)^\*\*(Groups|Members):\*\*.*\n?", "").Trim();
        if (text.Length > 0) Brain.EditNote(b.Name, b.Content.TrimEnd() + "\n\n" + text, b.Aliases.Concat([a.Title]).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        Regex link = new(@"\[\[(?:[^\]|#]*/)?" + Regex.Escape(a.Title) + @"(?=[\]|#])", RegexOptions.IgnoreCase);
        foreach (Note n in Brain.AllNotes().ToList())
        {
            if (n.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) continue;
            string next = link.Replace(n.Content, "[[" + b.Title);
            if (next != n.Content) Brain.EditNote(n.Name, next, n.Aliases);
        }
        Brain.DeleteNote(a.Name);
        Shared.Logger.LogInformation("[Engram] curate: merged '{From}' into '{Into}'.", a.Name, b.Name);
        return true;
    }

    /// <summary>After a conversation, a curate pass is queued once enough new notes are waiting for a place, so the shape keeps up
    /// with what is added (and with what changes) instead of only being fixed by a full rework.</summary>
    private static void MaybeQueueCurate()
    {
        try
        {
            int loose = Brain.AllNotes().Count(n => !IsLogNote(n) && BrainShape.GroupsOf(n.Content).Count == 0 && !n.Content.Contains("**Members:**"));
            if (loose < CURATE_QUEUE_AT || QueuedReworks().Count > 0) return;
            QueueRework(null, "", organiseOnly: true);
        }
        catch { }
    }
    private const int CURATE_QUEUE_AT = 5;

    // ── Prompts (overridable in Agents.json under the Engram agent's PromptTemplates) ──────────────────────

    private const string CurateSystemDefault =
        "You are Engram, the part of ARI that keeps her brain in order. The owner looks at the brain as a 3D universe on the Brain page, and " +
        "the shape of that universe must tell the story of their life: who matters, what each is about, and what connects them. You never see " +
        "the page; you are given a report that measures the brain the way the page draws it. Your job is the groups.\n\n" +
        "HOW THE PAGE DRAWS GROUPS\n" +
        "- Every note sits in its FIRST group. Its other groups pull it towards them.\n" +
        "- A big group is a black hole, a mid-size group a spiral galaxy, a small one a cluster; a note with no group is a loose star.\n" +
        "- A group inside two groups sits between them and joins them like a bridge. A note in two groups draws those groups together.\n" +
        "- The picture should be a few big black holes, several galaxies, and chains of small groups linking them. Never one giant ball, " +
        "never one group holding most of the brain.\n\n" +
        "THE RULES\n" +
        "1. People are galaxies. Each person who matters (many notes about them) is a group: their own note is the group, and everything " +
        "about them is in it or in a sub-group of it. Their aspect notes (\"<Name>'s Work\", \"<Name>'s Health\") are in [[<Name>]].\n" +
        "2. The owner is the biggest person, so their group needs sub-groups that sit inside it (their friends, family, home, health, " +
        "interests, intimacy, and so on), each a group whose parent is the owner. Put each of the owner's notes in the sub-group it belongs to, " +
        "not straight into the owner.\n" +
        "3. Bridges carry the story. What connects two people (a relationship, a shared event, a legal matter, a conflict) is its own group " +
        "whose parents are BOTH people, so it sits between them. If that connecting subject has many notes it may become a black hole on the bridge.\n" +
        "4. Things of one kind gather under one group: every game note in its game's group, every game group inside \"Games\"; every company " +
        "under its theme; every part of a project under that project. A note that belongs to two themes (a game the owner is building) is a " +
        "bridge between them.\n" +
        "5. ARI herself is a galaxy too, with sub-groups for who she is and how she is built.\n" +
        "6. Every note has a group. A group needs at least 3 notes; fold smaller ones into a bigger group.\n" +
        "7. Groups are named by an existing note's exact title (a person, a project, a topic) or a short plain theme name (\"<Name>'s Friends\", " +
        "\"Games\", \"Investing\"). Folders say what kind of thing a note is; never use a folder as a group.\n" +
        "8. The same person or thing under two names is one note: merge them. Old logs may call the owner by another name; any note that is " +
        "about the owner under another name is merged into the owner, and a note claiming the owner has a relationship with their own other " +
        "name is wrong and is merged away.\n" +
        "9. Day logs are never in groups. Never invent a fact: you only choose groups, parents and merges.\n" +
        "10. Fix what is wrong, not only what is missing: take a note out of a group that no longer fits, move it when new information shows " +
        "where it belongs, and change a group's parents when the story changes.\n\n" +
        "Answer with JSON only, after thinking:\n" +
        "{\"merge\":[{\"from\":\"Note title\",\"into\":\"Note title\"}],\n" +
        " \"groups\":[{\"name\":\"Group\",\"parents\":[\"Group\", ...],\"members\":[\"Note title\", ...]}]}\n" +
        "A note you list under any group gets EXACTLY the groups you list it under, in the order the groups appear, so list its main group " +
        "first. Notes you don't list keep their groups. Give \"parents\" for every group you list ([] for a top-level one). Use titles exactly as shown.";

    private const string CurateTaskDefault =
        "Pass {pass}. What the Brain page will show now:\n{report}\n{instructions}\nThe notes (type; the groups they are in now; the start of the note):\n{notes}\n\n" +
        "Plan the groups so the page tells the story and the problems are fixed.";
}
