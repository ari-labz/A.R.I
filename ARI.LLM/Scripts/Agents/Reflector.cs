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
        /// <summary>Replies shorter than this are left alone unless they trip a banned phrase.</summary>
        public int MinChars { get; set; } = 240;
        public bool VerifyWork { get; set; } = true;
        public List<string> BannedPhrases { get; set; } = DefaultBanned.ToList();
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
            prompt.AppendLine("Find only real problems:");
            prompt.AppendLine("- it restates, sums up or echoes what the user just said");
            prompt.AppendLine("- it states a fact that is not in the user's message (an invented detail about what they are doing, where they are, or what happened)");
            prompt.AppendLine("- it explains or pads where one short line would do");
            prompt.AppendLine("- it gives advice nobody asked for, or tells the user what to do with their day");
            if (bannedHits.Any()) prompt.AppendLine("- it uses a phrase that must never be used: " + string.Join("; ", bannedHits.Select(h => $"\"{h}\"")));
        }
        else
        {
            prompt.AppendLine($"You check {aiName}'s finished report on her work for claims that are not verifiable.");
            prompt.AppendLine("EVIDENCE below is the tool output from this turn. A claim is fine if the evidence, the user's message, or plain reasoning supports it, or if it is only a plan or an opinion.");
            prompt.AppendLine("A claim is a problem if it says something was done, built, tested, fixed, found or pushed and the evidence does not show it.");
            prompt.AppendLine("For each problem, edit the reply: reword that sentence so it says it is not verified (for example \"i haven't run the tests yet\"), or delete it.");
        }
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
            return Parse(answer, reply);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Shared.Logger.LogWarning("[Reflector] couldn't review a reply: {Err}", ex.Message); return new(); }
    }

    /// <summary>The edits in an answer, kept only when the text to replace really is in the reply, and only once each.</summary>
    internal static List<ReplyEdit> Parse(string answer, string reply)
    {
        List<ReplyEdit> edits = new();
        if (answer.TrimStart().StartsWith("OK", StringComparison.OrdinalIgnoreCase) && !answer.Contains("EDIT")) return edits;
        foreach (Match m in EditBlock.Matches(answer))
        {
            string old = m.Groups["old"].Value.Trim(), repl = m.Groups["new"].Value.Trim();
            if (old.Length == 0 || old == repl) continue;
            int first = reply.IndexOf(old, StringComparison.Ordinal);
            if (first < 0 || reply.IndexOf(old, first + 1, StringComparison.Ordinal) >= 0) continue;   // missing, or ambiguous
            if (edits.Any(e => e.Old.Contains(old) || old.Contains(e.Old))) continue;                   // overlapping edits would fight
            edits.Add(new ReplyEdit(old, repl));
            if (edits.Count == MaxEdits) break;
        }
        return edits;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[^max..];
}
