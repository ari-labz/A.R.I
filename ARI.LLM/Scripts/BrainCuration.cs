using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.BrainVault;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>A change to the brain that the curator suggests and the owner approves or dismisses. Nothing is changed until it is approved.</summary>
public sealed class BrainProposal
{
    public string   Id      { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>merge (fold note A into note B), type (give note A a type), link (link note B to note A, which nothing points at yet),
    /// stale (note A says Ari can't do something her tools now cover: sentence B becomes Value).</summary>
    public string   Kind    { get; set; } = "";
    public string   A       { get; set; } = "";
    public string   B       { get; set; } = "";
    public string   Value   { get; set; } = "";
    public string   Why     { get; set; } = "";
    /// <summary>pending, approved, dismissed or failed.</summary>
    public string   Status  { get; set; } = "pending";
    public string?  Result  { get; set; }
}

/// <summary>
/// Keeps the brain tidy while Ari is idle. It reads the tidiness scan (probable duplicates, notes with no type, notes nothing links to),
/// asks the reflector's model about a few at a time, and leaves concrete suggestions in the Brain tab. Approving one makes exactly that
/// change; dismissing it means it is never suggested again.
/// </summary>
internal static class BrainCuration
{
    private static readonly object Gate = new();
    // A dev run curates a throwaway copy of the brain, so its suggestions are kept apart: approving one there must not mark it done for the real brain.
    private static string StorePath => Path.Combine(Paths.PersistentData, Shared.DevMode ? "BrainProposals.devrun.json" : "BrainProposals.json");
    private static readonly string[] Types = ["person", "place", "event", "relationship", "concept", "project", "thing"];

    internal static List<BrainProposal> List()
    {
        lock (Gate)
        {
            try { if (File.Exists(StorePath)) return JsonSerializer.Deserialize<List<BrainProposal>>(File.ReadAllText(StorePath)) ?? new(); } catch { }
            return new();
        }
    }

    private static void Save(List<BrainProposal> all) { lock (Gate) File.WriteAllText(StorePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); }

    private static bool Seen(List<BrainProposal> all, string kind, string a, string b = "") =>
        all.Any(p => p.Kind == kind && ((p.A == a && p.B == b) || (p.A == b && p.B == a && kind == "merge")));

    /// <summary>Looks for up to <paramref name="max"/> new suggestions. Returns how many it added.</summary>
    internal static async Task<int> Suggest(Agent model, int max, CancellationToken ct)
    {
        if (!Brain.Ready) return 0;
        List<BrainProposal> all = List();
        if (all.Count(p => p.Status == "pending") >= 12) return 0;
        BrainHealthReport scan = BrainHealth.Scan();
        List<BrainProposal> fresh = new();

        foreach ((string a, string b) in scan.DuplicateCandidates)
        {
            if (fresh.Count >= max) break;
            if (Seen(all, "merge", a, b) || Brain.GetNote(a) is not { } na || Brain.GetNote(b) is not { } nb) continue;
            string answer = await Ask(model, $"Two notes in a personal knowledge graph have similar titles.\n\nNOTE \"{na.Title}\":\n{Clip(na.Content)}\n\nNOTE \"{nb.Title}\":\n{Clip(nb.Content)}\n\n" +
                "Are they about the same single thing? If yes, answer exactly: SAME | <title of the note to keep> | <one-sentence reason>. If not, answer exactly: DIFFERENT", ct);
            Match m = Regex.Match(answer, @"SAME\s*\|\s*(?<keep>[^|]+)\|\s*(?<why>.+)", RegexOptions.IgnoreCase);
            if (!m.Success) { all.Add(new BrainProposal { Kind = "merge", A = a, B = b, Status = "dismissed", Why = "judged different" }); continue; }
            string keep = m.Groups["keep"].Value.Trim().Trim('"');
            (string from, string into) = keep.Equals(na.Title, StringComparison.OrdinalIgnoreCase) ? (nb.Title, na.Title) : (na.Title, nb.Title);
            fresh.Add(new BrainProposal { Kind = "merge", A = from, B = into, Why = m.Groups["why"].Value.Trim() });
        }

        foreach (string t in scan.Untyped)
        {
            if (fresh.Count >= max) break;
            if (Seen(all, "type", t) || Brain.GetNote(t) is not { } n) continue;
            string answer = await Ask(model, $"NOTE \"{n.Title}\":\n{Clip(n.Content)}\n\nWhat kind of thing is this note about? Choose one of: {string.Join(", ", Types)}.\nAnswer exactly: TYPE | <kind> | <one-sentence reason>", ct);
            Match m = Regex.Match(answer, @"TYPE\s*\|\s*(?<k>\w+)\s*\|\s*(?<why>.+)", RegexOptions.IgnoreCase);
            if (!m.Success || !Types.Contains(m.Groups["k"].Value.ToLowerInvariant())) continue;
            fresh.Add(new BrainProposal { Kind = "type", A = n.Title, Value = m.Groups["k"].Value.ToLowerInvariant(), Why = m.Groups["why"].Value.Trim() });
        }

        foreach (string t in scan.Orphans)
        {
            if (fresh.Count >= max) break;
            if (all.Any(p => p.Kind == "link" && p.A == t) || Brain.GetNote(t) is not { } n) continue;
            if (n.Name.StartsWith("Conversations/", StringComparison.OrdinalIgnoreCase)) continue;   // diary days are reached through their month, not from each other
            string[] words = Regex.Split(n.Title + " " + string.Join(" ", n.Keywords), @"\W+").Where(w => w.Length > 2).Distinct().Take(6).ToArray();
            List<string> candidates = words.Length == 0 ? new() : Brain.Search(words, 10).Select(r => r.Note.Title).Where(x => !x.Equals(n.Title, StringComparison.OrdinalIgnoreCase)).Distinct().Take(8).ToList();
            if (candidates.Count == 0) { all.Add(new BrainProposal { Kind = "link", A = t, Status = "dismissed", Why = "no candidates" }); continue; }
            string answer = await Ask(model, $"NOTE \"{n.Title}\":\n{Clip(n.Content)}\n\nNothing in the knowledge graph links to this note yet. Which ONE of these notes should link to it, because it is closely about the same person, place or thing?\n" +
                string.Join("\n", candidates.Select(c => "- " + c)) + "\n\nAnswer exactly: LINK | <title from the list> | <one-sentence reason>, or NONE if none fits.", ct);
            Match m = Regex.Match(answer, @"LINK\s*\|\s*(?<to>[^|]+)\|\s*(?<why>.+)", RegexOptions.IgnoreCase);
            string? from = m.Success ? candidates.FirstOrDefault(c => c.Equals(m.Groups["to"].Value.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase)) : null;
            if (from is null) { all.Add(new BrainProposal { Kind = "link", A = t, Status = "dismissed", Why = "no good link" }); continue; }
            fresh.Add(new BrainProposal { Kind = "link", A = t, B = from, Why = m.Groups["why"].Value.Trim() });
        }

        // What she once couldn't do and now can: a note that says so keeps telling her she can't.
        // Only a sentence where Ari herself says she can't: "I can't", "Ari cannot", "she has no way to" (the last only on Ari's own note).
        // Anything else saying "unable to" is about someone else, often private, and is never a candidate.
        Regex cant = new(@"[^.\n]*\b(?<who>I|Ari|ARI|A\.R\.I|she)\s+(can't|cannot|can not|is unable to|am unable to|isn't able to|has no way to|have no way to|doesn't have (a|the) (way|ability))\b[^.\n]*[.\n]?", RegexOptions.IgnoreCase);
        // Everything she can call: the tool factories plus the tools every conversation is given directly (switch_pipeline, remember, …).
        List<string> toolNames = ToolFactories.AllNames().Concat(ToolRegistry.SystemTools).Append("revert_file").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        string tools = string.Join(", ", toolNames);
        foreach (Note n in Brain.Search(["can't", "cannot", "unable", "ability"], 30).Select(r => r.Note).DistinctBy(n => n.Title))
        {
            if (fresh.Count >= max) break;
            if (n.IsSensitive) continue;
            bool ariNote = Regex.IsMatch(n.Title, @"^(ari|a\.r\.i)$", RegexOptions.IgnoreCase);
            foreach (Match sm in cant.Matches(n.Content).Cast<Match>().Take(3))
            {
                // The whole paragraph it sits in is what gets rewritten, so the rest of it can't go on saying the opposite.
                int start = n.Content.LastIndexOf('\n', Math.Max(0, sm.Index - 1)) + 1, end = n.Content.IndexOf('\n', sm.Index);
                string sentence = n.Content[start..(end < 0 ? n.Content.Length : end)].Trim();
                if (sm.Groups["who"].Value.Equals("she", StringComparison.OrdinalIgnoreCase) && !ariNote) continue;
                if (sentence.Contains("[!sensitive]", StringComparison.OrdinalIgnoreCase) || sentence.TrimStart().StartsWith('>')) continue;
                if (sentence.Length < 12 || Seen(all, "stale", n.Title, sentence)) continue;
                string answer = await Ask(model, $"A paragraph in Ari's memory (Ari is an AI assistant) says:\n\"{sentence}\"\n\nFrom the note \"{n.Title}\":\n{Clip(n.Content)}\n\n" +
                    $"Ari's tools today are: {tools}.\n\nIs this paragraph about something ARI herself can't do, which one of those tools now lets her do? " +
                    "If yes, answer exactly: STALE | <the whole paragraph rewritten to be true now, same voice and detail, nothing left that says she can't, no tool names> | <one-sentence reason naming the tool>. Otherwise answer exactly: FINE", ct);
                Match m = Regex.Match(answer, @"STALE\s*\|\s*(?<fix>[^|]+)\|\s*(?<why>.+)", RegexOptions.IgnoreCase);
                bool namesATool = m.Success && toolNames.Any(t => m.Groups["why"].Value.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!namesATool) { all.Add(new BrainProposal { Kind = "stale", A = n.Title, B = sentence, Status = "dismissed", Why = "still true" }); continue; }
                fresh.Add(new BrainProposal { Kind = "stale", A = n.Title, B = sentence, Value = m.Groups["fix"].Value.Trim(), Why = m.Groups["why"].Value.Trim() });
                if (fresh.Count >= max) break;
            }
        }

        all.AddRange(fresh);
        Save(all);
        return fresh.Count;
    }

    /// <summary>Approves (makes the change) or dismisses a suggestion.</summary>
    internal static BrainProposal? Decide(string id, bool approve)
    {
        List<BrainProposal> all = List();
        BrainProposal? p = all.FirstOrDefault(x => x.Id == id && x.Status == "pending");
        if (p is null) return null;
        if (!approve) { p.Status = "dismissed"; Save(all); return p; }
        try
        {
            switch (p.Kind)
            {
                case "merge":
                    if (!Brain.MergeNotes(p.A, p.B)) throw new InvalidOperationException("one of the notes is gone");
                    p.Result = $"Merged \"{p.A}\" into \"{p.B}\".";
                    break;
                case "type":
                    Note n = Brain.GetNote(p.A) ?? throw new InvalidOperationException("the note is gone");
                    Brain.EditNote(p.A, n.Content, n.Aliases, type: p.Value);
                    p.Result = $"\"{p.A}\" is now a {p.Value}.";
                    break;
                case "link":
                    Note target = Brain.GetNote(p.B) ?? throw new InvalidOperationException("the note is gone");
                    string bare = p.A.Contains('/') ? p.A[(p.A.LastIndexOf('/') + 1)..] : p.A;
                    string body = target.Content.TrimEnd();
                    body = Regex.IsMatch(body, @"^## Related\s*$", RegexOptions.Multiline)
                        ? Regex.Replace(body, @"(^## Related\s*\n(?:- .*\n?)*)", m => m.Value.TrimEnd() + $"\n- [[{bare}]]\n", RegexOptions.Multiline)
                        : body + $"\n\n## Related\n- [[{bare}]]\n";
                    Brain.EditNote(p.B, body, target.Aliases);
                    p.Result = $"\"{p.B}\" now links to \"{p.A}\".";
                    break;
                case "stale":
                    Note old = Brain.GetNote(p.A) ?? throw new InvalidOperationException("the note is gone");
                    if (!old.Content.Contains(p.B)) throw new InvalidOperationException("the note no longer says that");
                    Brain.EditNote(p.A, old.Content.Replace(p.B, p.Value), old.Aliases);
                    p.Result = $"\"{p.A}\" now says: {p.Value}";
                    break;
                default: throw new InvalidOperationException("unknown kind");
            }
            p.Status = "approved";
            Shared.Logger.LogInformation("[BrainCuration] {Result}", p.Result);
        }
        catch (Exception ex) { p.Status = "failed"; p.Result = ex.Message; }
        Save(all);
        return p;
    }

    private static async Task<string> Ask(Agent model, string text, CancellationToken ct)
    {
        try
        {
            Thread scratch = new(ThreadPipeline.Dialogue, $"curate:{Guid.NewGuid():N}") { Internal = true, ReadOnlyTools = true };
            return await model.Prompt(scratch, text, new PromptOptions { MaxTokensOverride = 200, Ct = ct });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Shared.Logger.LogWarning("[BrainCuration] couldn't ask: {Err}", ex.Message); return ""; }
    }

    private static string Clip(string s) => s.Length <= 1500 ? s : s[..1500] + "…";
}
