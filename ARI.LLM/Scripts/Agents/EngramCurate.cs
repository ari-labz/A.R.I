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
    private const int MAX_MERGES        = 8;     // per plan: a merge is the one change that is hard to undo, so a plan gets only a few
    private const int CURATE_MAX_TOKENS = 15000;
    private const int SNIPPET_MIN = 50, SNIPPET_MAX = 600;   // characters of each note shown, scaled to the server's context

    /// <summary>How much of each note fits: the server's context, less the answer, the prompt and the report, shared between the notes
    /// (about 3.5 characters a token). A 32k slot sees a line of each note; a big server sees several sentences.</summary>
    private int SnippetLength(int notes)
    {
        int context = BudgetContext > 0 ? BudgetContext : 32768;
        int room = context - CURATE_MAX_TOKENS - 6000;
        int perNote = notes > 0 ? (int)(room * 3.5 / notes) - 60 : SNIPPET_MAX;   // 60: the title, type and groups shown before it
        return Math.Clamp(perNote, SNIPPET_MIN, SNIPPET_MAX);
    }

    private sealed record Hood(string Name, int Size, string Kind, List<string> Parents);

    /// <summary>Plans and applies the brain's groups, up to a few passes. Returns notes changed, or -1 when the model didn't answer.</summary>
    private async Task<int> Curate(string instructions)
    {
        int changedAll = 0;
        // A group named after a real note IS that note. The index keeps one note per title, so a Groups/ hub with a real note's name
        // hides that note completely: fold such hubs into the real file on disk (keeping any text they hold), then index again.
        for (int pass = 1; pass <= CURATE_PASSES; pass++)
        {
            changedAll += FoldShadowHubs();
            changedAll += NormaliseGroupNames();
            BrainShape.SyncGroups();
            List<Note> notes = Brain.AllNotes().Where(n => !IsLogNote(n)).ToList();
            if (notes.Count < 4) return changedAll;
            (string report, bool good, int scoreBefore) = ShapeReport(notes);
            if (good && pass > 1) break;
            // a pass may only improve the brain: mark where it starts so a worse result can be undone
            Commit($"Curate: before pass {pass}");
            (int _, string head, string _) = AriGit.Run(Brain.VaultRoot, ["rev-parse", "HEAD"]);

            int snippet = SnippetLength(notes.Count);
            string listing = string.Join("\n", notes.Select(n =>
            {
                string text = Regex.Replace(Regex.Replace(n.Content, @"(?m)^\*\*(Groups|Members):\*\*.*$", ""), @"\s+", " ").Trim();
                List<string> gs = BrainShape.GroupsOf(n.Content).Select(GroupTitle).ToList();
                return $"- {n.Title} ({n.Type ?? "untyped"}{(gs.Count > 0 ? "; in " + string.Join(", ", gs) : "")}): {(text.Length > snippet ? text[..snippet] + "…" : text)}";
            }));
            string? answer = await CompleteThinking(ResolveTemplate("CurateSystem", CurateSystemDefault),
                ResolveTemplate("CurateTask", CurateTaskDefault, ("report", report), ("notes", listing), ("pass", pass.ToString()),
                    ("instructions", instructions.Length > 0 ? $"\nThe owner's instructions: {instructions}\n" : "")), CURATE_MAX_TOKENS);
            if (answer is null) return pass == 1 ? -1 : changedAll;
            if (!ParsesAsPlan(answer))
            {
                // Thinking ran out before the answer: once more, straight to the plan.
                Shared.Logger.LogInformation("[Engram] curate pass {Pass}: no plan in the answer; asking again without thinking.", pass);
                answer = await CompleteThinking(ResolveTemplate("CurateSystem", CurateSystemDefault),
                    ResolveTemplate("CurateTask", CurateTaskDefault, ("report", report), ("notes", listing), ("pass", pass.ToString()),
                        ("instructions", instructions.Length > 0 ? $"\nThe owner's instructions: {instructions}\n" : "")) + "\n\nAnswer with the JSON plan now.", CURATE_MAX_TOKENS, think: false) ?? "";
            }
            try { File.WriteAllText(Path.Combine(QueueDir, $"curate-{DateTime.Now:yyyyMMdd-HHmmss}-pass{pass}.txt"), report + "\n\n=====\n" + answer); } catch { }   // kept so a run can be graded
            int changed = ApplyPlan(OuterJson(answer, '{', '}'), notes.Count) + EnsureBridges();
            BrainShape.SyncGroups();
            (_, _, int scoreAfter) = ShapeReport(Brain.AllNotes().Where(n => !IsLogNote(n)).ToList());
            Shared.Logger.LogInformation("[Engram] curate pass {Pass}: {N} change(s), score {Before} → {After} (lower is better).", pass, changed, scoreBefore, scoreAfter);
            if (scoreAfter > scoreBefore && head.Trim().Length > 0)
            {
                AriGit.Run(Brain.VaultRoot, ["reset", "--hard", head.Trim()]);
                AriGit.Run(Brain.VaultRoot, ["clean", "-fd"]);
                Brain.Index();
                Shared.Logger.LogInformation("[Engram] curate pass {Pass} made the brain worse; undone. Keeping the best shape so far.", pass);
                continue;   // another look from the best shape: the model's next plan may be better
            }
            if (changed == 0) break;
            changedAll += changed;
        }
        int written = BrainShape.SyncGroups();
        if (changedAll > 0) Commit($"Curate: reshape the brain's groups ({changedAll} change(s), {written} group(s) updated)");
        (string final, _, _) = ShapeReport(Brain.AllNotes().Where(n => !IsLogNote(n)).ToList());
        Shared.Logger.LogInformation("[Engram] curate done:\n{Report}", final);
        return changedAll;
    }

    /// <summary>Groups written as plain titles, never folders, and always under the note's own name (an alias such as an old name of the
    /// owner resolves to the owner), so one subject is one group.</summary>
    private static int NormaliseGroupNames()
    {
        int changed = 0;
        foreach (Note n in Brain.AllNotes().Where(n => !IsLogNote(n)).ToList())
        {
            List<string> gs = BrainShape.GroupsOf(n.Content);
            List<string> clean = gs.Select(GroupName).Where(g => g.Length > 0 && !g.Equals(n.Title, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!clean.SequenceEqual(gs)) changed += SetGroups(n.Name, clean);
        }
        return changed;
    }

    private static bool ParsesAsPlan(string answer)
    {
        if (OuterJson(answer, '{', '}') is not { } json) return false;
        try { using JsonDocument d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("groups", out _) || d.RootElement.TryGetProperty("merge", out _); }
        catch (JsonException) { return false; }
    }

    private static readonly Regex RelationshipTitle = new(@"^(?<a>.+?) and (?<b>.+?) (Relationship|Friendship)$", RegexOptions.IgnoreCase);

    /// <summary>Every bond between two people becomes a bridge: a group "A and B" inside both A's and B's groups, holding the relationship
    /// note first. Done by code, not left to the model, so no run of the curator can lose the story between two people.</summary>
    private static int EnsureBridges()
    {
        int changed = 0;
        foreach (Note rel in Brain.AllNotes().Where(n => n.Name.StartsWith("Relationships/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Match m = RelationshipTitle.Match(rel.Title);
            if (!m.Success || Brain.GetNote(m.Groups["a"].Value) is not { } a || Brain.GetNote(m.Groups["b"].Value) is not { } b) continue;
            // only between two real notes: a name that is just a group hub (an old name already merged away) makes no bridge
            if (a.Name.StartsWith("Groups/", StringComparison.OrdinalIgnoreCase) || b.Name.StartsWith("Groups/", StringComparison.OrdinalIgnoreCase)) continue;
            // a "relationship" between a person and their own other name is that person's own notes
            if (a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)) { if (MergeNotes(rel.Name, a.Name)) changed++; continue; }
            // one under an old name ("Al and Jo" when Al is the owner's old name) is the same bond as the one under the real names
            string real = $"{a.Title} and {b.Title} {m.Groups[3].Value}";
            if (!rel.Title.Equals(real, StringComparison.OrdinalIgnoreCase) && Brain.GetNote(real) is { } same && !same.Name.Equals(rel.Name, StringComparison.OrdinalIgnoreCase))
            { if (MergeNotes(rel.Name, same.Name)) changed++; continue; }
            string bridge = $"{a.Title} and {b.Title}";
            List<string> gs = BrainShape.GroupsOf(rel.Content).Select(GroupTitle).Where(g => !g.Equals(bridge, StringComparison.OrdinalIgnoreCase)).ToList();
            changed += SetGroups(rel.Name, gs.Prepend(bridge).ToList());
            BrainShape.SyncGroups();
            if (Brain.GetNote(bridge) is { } bn) changed += SetGroups(bn.Name, [a.Title, b.Title]);
        }
        return changed;
    }

    private static int FoldShadowHubs()
    {
        string root = Brain.VaultRoot, groups = Path.Combine(root, "Groups");
        if (!Directory.Exists(groups)) return 0;
        int folded = 0;
        foreach (string hub in Directory.GetFiles(groups, "*.md"))
        {
            string name = Path.GetFileName(hub);
            string? real = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Equals(hub, StringComparison.Ordinal) && !f.Contains("/.git/") && !f.Contains("/.ari/") && !f.Contains("/Groups/"));
            string title = Path.GetFileNameWithoutExtension(name);
            // or a note that has this name as an alias (the owner under an old name)
            real ??= Brain.AllNotes().FirstOrDefault(n => !n.Name.StartsWith("Groups/", StringComparison.OrdinalIgnoreCase) && n.Aliases.Contains(title, StringComparer.OrdinalIgnoreCase)) is { } aliased
                ? Path.Combine(root, aliased.Path) : null;
            if (real is null) continue;
            string text = File.ReadAllText(hub);
            text = Regex.Replace(text, @"^---\n.*?\n---\n", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"(?m)^\*\*(Groups|Members):\*\*.*\n?", "").Trim();
            if (text.Length > 0) File.AppendAllText(real, "\n\n" + text + "\n");
            File.Delete(hub);
            folded++;
            Shared.Logger.LogInformation("[Engram] curate: folded hub 'Groups/{Hub}' into the real note '{Real}'.", name, Path.GetRelativePath(root, real));
        }
        if (folded > 0) Brain.Index();
        return folded;
    }

    private static bool IsLogNote(Note n) =>
        n.Name.StartsWith("Conversations", StringComparison.OrdinalIgnoreCase) || n.Type == "discussion";

    private static string GroupTitle(string g) => g.Contains('/') ? g[(g.LastIndexOf('/') + 1)..] : g;

    // ── What the page will show ─────────────────────────────────────────────────────────────

    /// <summary>The brain measured the way the Brain page draws it, in words Engram can act on, and whether it already meets the rules.</summary>
    internal static (string Report, bool Good, int Score) ShapeReport(List<Note> notes)
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
        HashSet<string> people = notes.Where(n => n.Type == "person").Select(n => n.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Hood> tiny = hoods.Where(h => h.Kind == "too small to show" && h.Parents.Count < 2 && !people.Contains(h.Name)).ToList();   // a small bridge or a person is still meaningful
        List<Hood> bridges = hoods.Where(h => h.Parents.Count >= 2).ToList();
        List<string> peopleNotGroups = notes.Where(n => n.Type == "person" && !groupNames.Contains(n.Title))
            .Where(n => notes.Count(m => m.Content.Contains($"[[{n.Title}]]", StringComparison.OrdinalIgnoreCase)) >= 4).Select(n => n.Title).ToList();

        List<string> problems = new();
        // one group that every other group sits inside pulls the whole picture into one ball
        string? hub = hoods.Select(h => h.Parents.FirstOrDefault()).Where(p => p is not null).GroupBy(p => p!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        int under = hub is null ? 0 : hoods.Count(h => h.Parents.Count == 1 && h.Parents[0].Equals(hub, StringComparison.OrdinalIgnoreCase) && h.Size >= spiral);
        int bigTotal = hoods.Count(h => h.Size >= spiral && h.Parents.Count < 2);
        bool nested = bigTotal >= 4 && under > bigTotal / 2;
        if (nested) problems.Add($"{under} of the {bigTotal} big groups sit inside '{hub}', so everything is pulled into one ball. Keep only personal-life groups inside '{hub}'; make the big subjects (work, projects, games, investing, ARI) top-level groups with no parent.");
        // the same theme under two names ("Music" and "Sam's Music", "Health" and "Sam's Health") splits it into scraps
        string Core(string g) => Regex.Replace(Engram.GroupCanon(g.Contains("'s ") ? g[(g.IndexOf("'s ") + 3)..] : g), "s$", "");
        List<string> twins = groupNames.Where(g => !g.Contains(" and ")).GroupBy(Core).Where(x => x.Count() > 1).Select(x => string.Join(" / ", x)).ToList();
        if (twins.Count > 0) problems.Add($"These groups are the same theme under different names, so the theme is split: {string.Join("; ", twins)}. Keep one name for each and move all its notes there.");
        int relationships = notes.Count(n => n.Name.StartsWith("Relationships/", StringComparison.OrdinalIgnoreCase));
        if (relationships > 0 && bridges.Count == 0) problems.Add($"There are {relationships} relationship note(s) but no bridge groups: the people in the owner's life float apart from them. Make a \"<Owner> and <Name>\" group inside both people for each.");
        List<string> mixed = hoods.Where(h => h.Parents.Count < 2 && (h.Name.Contains(" & ") || h.Name.Contains(" and "))).Select(h => h.Name).ToList();
        if (mixed.Count > 0) problems.Add($"These groups mix two themes: {string.Join(", ", mixed)}. Split each into one group per theme (only bridges between two people are named \"A and B\").");
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
        // one number for "how far from the rules", so a pass that makes things worse can be undone
        int score = loose.Count * 2 + tiny.Count * 2 + twins.Count * 5 + mixed.Count * 5 + peopleNotGroups.Count * 3
            + (share > .3 ? (int)(share * 200) : 0) + (holes == 0 && notes.Count >= 60 ? 30 : 0) + (holes > Math.Max(6, notes.Count / 150) ? 15 : 0)
            + (relationships > 0 && bridges.Count == 0 ? 30 : 0) + (nested ? 25 : 0);
        return (sb.ToString().TrimEnd(), problems.Count == 0, score);
    }

    // ── Applying Engram's plan ────────────────────────────────────────────────────────────

    /// <summary>Applies a plan: merges first, then each listed note gets exactly the groups it is listed under (first listed = where it
    /// sits), and each listed group gets its parents. Notes the plan doesn't mention keep their groups. Returns the number of changes.</summary>
    private static int ApplyPlan(string? json, int noteCount)
    {
        if (json is null) return 0;
        int changed = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("merge", out JsonElement merges) && merges.ValueKind == JsonValueKind.Array)
                foreach (JsonElement m in merges.EnumerateArray().Take(MAX_MERGES))
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
                if (ms.GetArrayLength() > Math.Max(40, noteCount / 4))
                {   // a runaway list (most of the brain under one group) is a model slip, not a plan
                    Shared.Logger.LogInformation("[Engram] curate: ignored group '{Group}' — {N} members is most of the brain.", name, ms.GetArrayLength());
                    continue;
                }
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
        // Only the same thing under two names: never into or out of a group, and the same kind of note (person into person), except a
        // relationship built on the owner's other name, which folds into the person it names.
        static bool IsGroupNote(Note n) => n.Name.StartsWith("Groups/", StringComparison.OrdinalIgnoreCase) || n.Type == "group" || n.Content.Contains("**Members:**") && n.Type is null;
        if (IsGroupNote(a) || IsGroupNote(b)) { Shared.Logger.LogInformation("[Engram] curate: refused to merge '{From}' into '{Into}' (a group is not a note to merge).", a.Name, b.Name); return false; }
        string fa = a.Name.Contains('/') ? a.Name[..a.Name.IndexOf('/')] : "", fb = b.Name.Contains('/') ? b.Name[..b.Name.IndexOf('/')] : "";
        bool relIntoPerson = fa.Equals("Relationships", StringComparison.OrdinalIgnoreCase) && fb.Equals("People", StringComparison.OrdinalIgnoreCase) && a.Title.Contains(b.Title, StringComparison.OrdinalIgnoreCase);
        if (!fa.Equals(fb, StringComparison.OrdinalIgnoreCase) && !relIntoPerson) { Shared.Logger.LogInformation("[Engram] curate: refused to merge '{From}' into '{Into}' (different kinds of note).", a.Name, b.Name); return false; }
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
        "2. Only the owner's PERSONAL life sits inside the owner (their identity, health, intimacy, family, home, friends). Big subjects of their life (their work, each project, their games, their investing, ARI) are top-level groups with no parent: separate galaxies, joined to the owner by the notes that link them, never nested inside the owner. The owner is the biggest person, so their group needs sub-groups that sit inside it (their friends, family, home, health, " +
        "interests, intimacy, and so on), each a group whose parent is the owner. Put each of the owner's notes in the sub-group it belongs to, " +
        "not straight into the owner.\n" +
        "3. Bridges carry the story. For EVERY other person with a relationship, a shared event or a conflict with the owner, make a group named \"<Owner> and <Name>\" whose parents are [<Owner>, <Name>], and put the relationship note, the shared events and the matter between them in it.  What connects two people (a relationship, a shared event, a legal matter, a conflict) is its own group " +
        "whose parents are BOTH people, so it sits between them. If that connecting subject has many notes it may become a black hole on the bridge.\n" +
        "4. Things of one kind gather under one group: every game note in its game's group, every game group inside \"Games\"; every company " +
        "under its theme; every part of a project under that project. A note that belongs to two themes (a game the owner is building) is a " +
        "bridge between them.\n" +
        "5. ARI herself is a galaxy too, with sub-groups for who she is and how she is built.\n" +
        "6. Every note has a group. A group needs at least 3 notes; fold smaller ones into a bigger group.\n" +
        "7. One theme per group: never join two themes with \"&\" or \"and\" (\"Health & Intimacy\" is two groups). The only \"and\" names are bridges between two people. Groups are named by an existing note's exact title (a person, a project, a topic) or a short plain theme name (\"<Name>'s Friends\", " +
        "\"Games\", \"Investing\"). Folders say what kind of thing a note is; never use a folder as a group.\n" +
        "8. Merging is rare (at most a few per plan). Never merge a note into a group or a group into a note: groups are made by listing members, never by merging. Merge ONLY when two notes are certainly the same person or thing under two names (an alias, a misspelling, the owner under another name). Never merge a product into its maker, a part into its whole, a role or job title into a person, or two people who might be different. When in any doubt, do not merge. Old logs may call the owner by another name; any note that is " +
        "about the owner under another name is merged into the owner, and a note claiming the owner has a relationship with their own other " +
        "name is wrong and is merged away.\n" +
        "9. Day logs are never in groups. Never invent a fact: you only choose groups, parents and merges.\n" +
        "10. Fix what is wrong, not only what is missing: take a note out of a group that no longer fits, move it when new information shows " +
        "where it belongs, and change a group's parents when the story changes.\n\n" +
        "Think briefly: decide the main groups and the moves, then write the JSON. Keep thinking short so the answer fits.\n" +
        "Answer with JSON only:\n" +
        "{\"merge\":[{\"from\":\"Note title\",\"into\":\"Note title\"}],\n" +
        " \"groups\":[{\"name\":\"Group\",\"parents\":[\"Group\", ...],\"members\":[\"Note title\", ...]}]}\n" +
        "Group names: one theme each, never two joined with \"&\" (write \"Health\" and \"Identity\" as two groups, not \"Health & Identity\"); " +
        "never a folder name (People, Topics, Projects, Events, Places, Relationships). A bridge \"<Owner> and <Name>\" holds only what is between those two " +
        "(their relationship note, shared events, the matter between them), never either person's own notes or aspects.\n" +
        "A note you list under any group gets EXACTLY the groups you list it under, in the order the groups appear, so list its main group " +
        "first. Notes you don't list keep their groups. Give \"parents\" for every group you list ([] for a top-level one). Use titles exactly as shown.";

    private const string CurateTaskDefault =
        "Pass {pass}. What the Brain page will show now:\n{report}\n{instructions}\nThe notes (type; the groups they are in now; the start of the note):\n{notes}\n\n" +
        "Plan the groups so the page tells the story and the problems are fixed.";
}
