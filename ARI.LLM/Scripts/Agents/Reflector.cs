using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>How the reflector reads a reply: as a chat message (is it padded, echoing, inventing?) or as a report on work (is every claim shown by the evidence?).</summary>
internal enum ReflectMode { Chat, Work }

/// <summary>One change the reflector wants made to a reply: replace this exact text with that (empty = delete it).</summary>
internal sealed record ReplyEdit(string Old, string New);

/// <summary>Switches and limits for the reflect stage, read from Reflect.json in the data folder so they can be changed without a rebuild.</summary>
internal static class ReflectSettings
{
    private static string FilePath => Path.Combine(Paths.PersistentData, "Reflect.json");

    // Phrases her persona says never to use. Finding one is reason enough to look at a reply even if it is short.
    private static readonly string[] DefaultBanned =
    {
        "isn't going anywhere", "i'll be here", "heavy lifting", "the part where", "that's not nothing", "that tracks", "makes sense",
        "hang in there", "you've got this", "future-you will thank you",
    };

    internal sealed class Values
    {
        public bool Enabled { get; set; } = true;
        /// <summary>How an edit looks in the app: cursor (a named second author), retype, select or suggest.</summary>
        public string EditStyle { get; set; } = "cursor";
        /// <summary>Replies shorter than this are left alone unless they trip a banned phrase.</summary>
        public int MinChars { get; set; } = 240;
        public bool VerifyWork { get; set; } = true;
        /// <summary>While she writes, a paragraph is sent for review once it is at least this long.</summary>
        public int LiveMinChars { get; set; } = 140;
        public List<string> BannedPhrases { get; set; } = DefaultBanned.ToList();
    }

    internal static readonly string[] EditStyles = { "cursor", "retype", "select", "suggest" };

    internal static void Save(Values v)
    {
        if (!EditStyles.Contains(v.EditStyle)) v.EditStyle = "cursor";
        File.WriteAllText(FilePath, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static Values Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Values>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { }
        return new();
    }
}

/// <summary>
/// Reads a finished reply before it is final and says what, if anything, should change. The same loop serves two jobs: tidying a chat
/// reply (draft, reflect, send) and checking a report on work against the evidence (verify before done). It never rewrites a reply
/// whole: it returns exact edits, which the app shows being made.
/// </summary>
internal class Reflector : Agent
{
    public Reflector() { }
    internal static Reflector? Current { get; set; }
    internal override bool SuppressLog => true;

    private const int MaxEdits = 3;
    private static readonly Regex EditBlock = new(@"EDIT\s*\r?\nold:(?<old>.*?)\r?\nnew:(?<new>.*?)\r?\nEND", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Edits to make, or none when the reply is fine (or the reflector couldn't be reached: a reply is never held up by it failing).</summary>
    internal async Task<List<ReplyEdit>> Review(ReflectMode mode, string aiName, string userMessage, string reply, string evidence, IEnumerable<string> bannedHits, CancellationToken ct)
    {
        StringBuilder prompt = new();
        if (mode == ReflectMode.Chat)
        {
            prompt.AppendLine($"You proofread a chat reply before it is final. {aiName} texts like a quick, casual friend: short replies, plain words.");
            prompt.AppendLine("Most replies are fine. Edit only where one of these is clearly true:");
            prompt.AppendLine("- it restates, sums up or echoes what the user just said");
            prompt.AppendLine("- it invents what the user is doing, where they are, or what happened to them");
            prompt.AppendLine("- a casual reply explains or pads where one short line would do");
            prompt.AppendLine("- it gives advice nobody asked for, or tells the user what to do with their day");
            if (bannedHits.Any()) prompt.AppendLine("- it uses a phrase that must never be used: " + string.Join("; ", bannedHits.Select(h => $"\"{h}\"")));
            prompt.AppendLine("Never touch writing the user asked for (a story, poem, description or letter): its imagery and detail are the point, not padding. Never touch her feelings, warmth or reassurance.");
        }
        else
        {
            prompt.AppendLine($"You check {aiName}'s finished report on her work for claims about the work that the evidence does not support.");
            prompt.AppendLine("Lines starting FACT are certain: treat them as true. If a FACT says nothing was built, tested or run after the changes, then any claim that the change compiles, passes, works or is tested is a problem.");
            prompt.AppendLine("EVIDENCE below is the tool output from this turn. A claim is a problem only if it says something was done, built, tested, fixed, found or pushed and the evidence does not show it.");
            prompt.AppendLine("Everything else is never a problem: plans, opinions, explanations, feelings, promises, reassurance, and anything she says about the user or the conversation.");
            prompt.AppendLine("Fix a problem by rewording the sentence so it claims only what was done (for example \"i haven't run the tests yet\"), or by deleting it.");
        }
        prompt.AppendLine();
        prompt.AppendLine("Every edit must leave whole, natural sentences in her voice: lowercase and casual like the rest of the reply. To drop a phrase, rewrite the sentence around it rather than leaving a fragment. Never add a tag or note such as \"(not verified)\" or \"(verified)\".");
        prompt.AppendLine();
        prompt.AppendLine("If there is nothing to fix, answer with only: OK");
        prompt.AppendLine($"Otherwise give at most {MaxEdits} edits, each in exactly this form, with old copied exactly from the reply and new empty to delete it:");
        prompt.AppendLine("EDIT\nold: <exact text from the reply>\nnew: <replacement>\nEND");
        prompt.AppendLine();
        prompt.AppendLine("User's message:\n" + Clip(userMessage, 700));
        if (mode == ReflectMode.Work) prompt.AppendLine("\nEVIDENCE:\n" + (evidence.Length == 0 ? "(no tools were used this turn)" : evidence));
        prompt.AppendLine("\nReply:\n" + reply);

        try
        {
            Thread ephemeral = new(ThreadPipeline.Dialogue, $"__reflect_{Guid.NewGuid():N}") { Internal = true };
            string answer = await Prompt(ephemeral, prompt.ToString(), new PromptOptions { MaxTokensOverride = 500, Ct = ct });
            return Parse(answer, reply, userMessage);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Shared.Logger.LogWarning("[Reflector] couldn't review a reply: {Err}", ex.Message); return new(); }
    }

    /// <summary>The edits in an answer, kept only when the text to replace really is in the reply, and only once each.</summary>
    internal static List<ReplyEdit> Parse(string answer, string reply, string userMessage = "")
    {
        List<ReplyEdit> edits = new();
        if (answer.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase) && !answer.Contains("EDIT")) return edits;
        foreach (Match m in EditBlock.Matches(answer))
        {
            string old = m.Groups["old"].Value.Trim(), repl = m.Groups["new"].Value.Trim();
            if (old.Length == 0 || old == repl) continue;
            if (repl.StartsWith(old, StringComparison.Ordinal)) continue;   // only tacks something on, like "(not verified)": never a fix
            int first = reply.IndexOf(old, StringComparison.Ordinal);
            if (first < 0 || reply.IndexOf(old, first + 1, StringComparison.Ordinal) >= 0) continue;   // missing, or ambiguous
            if (edits.Any(e => e.Old.Contains(old) || old.Contains(e.Old))) continue;                   // overlapping edits would fight
            // A fix never pastes the user's own words over hers (a message that says "repeat this back" once turned whole paragraphs into
            // "i'm tired."), and never gives several different passages the same replacement.
            if (repl.Length > 5 && userMessage.Contains(repl, StringComparison.OrdinalIgnoreCase) && !old.Contains(repl, StringComparison.OrdinalIgnoreCase)) continue;
            if (repl.Length > 0 && edits.Any(e => e.New == repl)) continue;
            edits.Add(new ReplyEdit(old, repl));
            if (edits.Count == MaxEdits) break;
        }
        return edits;
    }

    /// <summary>Reads the corrections logged lately and suggests, at most three times over, what to change so she stops needing them: a phrase
    /// to never use, or a note for the owner. Returns nothing if the reflector can't be reached.</summary>
    internal async Task<List<ReflectionProposal>> Propose(IEnumerable<string> logLines, CancellationToken ct)
    {
        StringBuilder prompt = new();
        prompt.AppendLine("Below are corrections made to Ari's own replies before they were sent (each line: which kind of reply, and the exact text that was replaced and what replaced it).");
        prompt.AppendLine("Look for repeated patterns, and suggest at most 3 changes that would stop her needing the corrections. Use exactly this form for each:");
        prompt.AppendLine("PROPOSAL\nkind: banned_phrase  (a short phrase, under 8 words, she keeps using and should never use) OR note  (one plain sentence about a pattern, for her owner to read)\ntext: <the phrase, or the sentence>\nwhy: <one short reason from the corrections>\nEND");
        prompt.AppendLine("Only suggest what several corrections support. If nothing repeats, answer only: NONE");
        prompt.AppendLine();
        foreach (string l in logLines) prompt.AppendLine(Clip(l, 600));
        try
        {
            Thread ephemeral = new(ThreadPipeline.Dialogue, $"__reflect_{Guid.NewGuid():N}") { Internal = true };
            string answer = await Prompt(ephemeral, prompt.ToString(), new PromptOptions { MaxTokensOverride = 600, Ct = ct });
            List<ReflectionProposal> found = new();
            foreach (Match m in Regex.Matches(answer, @"PROPOSAL\s*\r?\nkind:\s*(?<k>\w+)\s*\r?\ntext:\s*(?<t>.*?)\r?\nwhy:\s*(?<w>.*?)\r?\nEND", RegexOptions.Singleline))
            {
                string kind = m.Groups["k"].Value.ToLowerInvariant() == "banned_phrase" ? "banned_phrase" : "note";
                string text = m.Groups["t"].Value.Trim().Trim('"');
                if (text.Length == 0) continue;
                found.Add(new ReflectionProposal { Kind = kind, Text = text, Why = m.Groups["w"].Value.Trim() });
                if (found.Count == 3) break;
            }
            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Shared.Logger.LogWarning("[Reflector] couldn't draw proposals: {Err}", ex.Message); return new(); }
    }

    /// <summary>Notes each correction in Reflections.jsonl (when, which thread, chat or work, what was changed), so patterns in what she gets wrong can be found later.</summary>
    internal static void Record(string thread, ReflectMode mode, IEnumerable<ReplyEdit> edits)
    {
        try
        {
            string line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow, thread, mode = mode.ToString(), edits = edits.Select(e => new { e.Old, e.New }) });
            File.AppendAllText(Path.Combine(Paths.PersistentData, "Reflections.jsonl"), line + "\n");
        }
        catch { }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[^max..];
}

/// <summary>A suggestion the Reflector has drawn from her own corrections, waiting for the owner to approve or dismiss it.
/// Nothing in here changes anything until it is approved, and only the "banned_phrase" kind can be applied by itself.</summary>
internal sealed class ReflectionProposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>"banned_phrase" (a phrase to add to the never-say list) or "note" (an observation for the owner to read).</summary>
    public string Kind { get; set; } = "note";
    public string Text { get; set; } = "";
    public string Why { get; set; } = "";
    /// <summary>pending, approved or dismissed.</summary>
    public string Status { get; set; } = "pending";
}

internal static class ReflectionProposals
{
    private static readonly object Gate = new();
    private static string ProposalsPath => Path.Combine(Paths.PersistentData, "ReflectionProposals.json");
    private static string LogPath       => Path.Combine(Paths.PersistentData, "Reflections.jsonl");
    private static string MarkPath      => Path.Combine(Paths.PersistentData, "ReflectionsAnalysed.txt");

    internal static List<ReflectionProposal> List()
    {
        lock (Gate)
        {
            try { if (File.Exists(ProposalsPath)) return JsonSerializer.Deserialize<List<ReflectionProposal>>(File.ReadAllText(ProposalsPath)) ?? new(); } catch { }
            return new();
        }
    }

    private static void Save(List<ReflectionProposal> all) { lock (Gate) File.WriteAllText(ProposalsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); }

    /// <summary>Approves or dismisses a proposal. Approving a banned phrase adds it to the reflector's never-say list.</summary>
    internal static bool Decide(string id, bool approve)
    {
        List<ReflectionProposal> all = List();
        ReflectionProposal? p = all.FirstOrDefault(x => x.Id == id && x.Status == "pending");
        if (p is null) return false;
        p.Status = approve ? "approved" : "dismissed";
        if (approve && p.Kind == "banned_phrase" && p.Text.Trim().Length is > 2 and < 80)
        {
            ReflectSettings.Values v = ReflectSettings.Load();
            if (!v.BannedPhrases.Contains(p.Text.Trim(), StringComparer.OrdinalIgnoreCase)) v.BannedPhrases.Add(p.Text.Trim());
            ReflectSettings.Save(v);
        }
        Save(all);
        return true;
    }

    /// <summary>The corrections logged since the last analysis, or none if there are fewer than <paramref name="min"/> of them.</summary>
    internal static List<string> Unanalysed(int min, out int total)
    {
        total = 0;
        if (!File.Exists(LogPath)) return new();
        string[] lines = File.ReadAllLines(LogPath);
        total = lines.Length;
        int done = int.TryParse(File.Exists(MarkPath) ? File.ReadAllText(MarkPath).Trim() : "0", out int d) ? d : 0;
        return lines.Length - done >= min ? lines.Skip(done).TakeLast(60).ToList() : new();
    }

    internal static void MarkAnalysed(int total) => File.WriteAllText(MarkPath, total.ToString());

    internal static void Add(IEnumerable<ReflectionProposal> fresh)
    {
        List<ReflectionProposal> all = List();
        foreach (ReflectionProposal p in fresh)
            if (!all.Any(x => x.Kind == p.Kind && string.Equals(x.Text, p.Text, StringComparison.OrdinalIgnoreCase))) all.Add(p);
        Save(all);
    }
}

