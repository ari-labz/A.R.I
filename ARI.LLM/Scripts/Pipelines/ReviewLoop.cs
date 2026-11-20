using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>One of a reviewer agent's pathway tools: a name, the arguments it takes, and the sentence the agent reads about it. Calling one
/// does nothing by itself; it only says which way the pipeline goes next.</summary>
public sealed class PathwayTool
{
    public string       Name        { get; set; } = "";
    public List<string> Args        { get; set; } = new();
    public string       Description { get; set; } = "";

    internal string Signature => $"{Name}({string.Join(", ", Args)})";
}

/// <summary>
/// Draft, reflect, send in a pipeline graph. The reply agent streams to the user as usual; when it has finished writing, the reviewer
/// wired to its output reads the reply and calls one of its pathway tools. Pass (or any pathway with no wire) ends the turn. A pathway
/// wired back to the writer, such as Fail(reason), gives the writer another go with the reason on its critique input; the new version
/// is turned into edits the user watches being made. The wire's round limit is a safety net: once it is used up the reviewer is offered
/// only the ending pathways and Settle(), and decides itself.
/// </summary>
internal sealed class ReviewLoop
{
    /// <summary>The loop for the reply being written in this async flow, if its pipeline has one.</summary>
    internal static readonly AsyncLocal<ReviewLoop?> Active = new();

    internal required Thread      Chat;
    internal required CustomAgent Reviewer;
    internal required Agent       Writer;
    internal required string      ReviewerReplyPort;
    internal required Func<Dictionary<string, string>> ReviewerInputs;   // its other inputs, read when it runs
    internal required string      Message;
    internal string?              LoopPathway;      // the pathway wired back to the writer (e.g. Fail)
    internal string               CritiquePort = "critique";
    internal int                  MaxRounds = 2;
    internal DateTime             UserSentAt = DateTime.Now;
    internal List<Step>           Steps = new();
    internal Stopwatch            Total = Stopwatch.StartNew();

    internal sealed record Step(string Who, string What, double Seconds, string Kind = "");

    private static readonly PathwayTool Settle = new() { Name = "Settle", Description = "You are out of attempts. End the turn and keep the reply as it now is." };

    /// <summary>Runs the review, round by round, inside the writer's turn. <paramref name="currentReply"/> reads the reply as it stands,
    /// <paramref name="apply"/> shows edits being made to it, and <paramref name="appendMarker"/> adds the run's timeline to the message.</summary>
    internal async Task Run(Func<string> currentReply, Func<List<ReplyEdit>, Task> apply, Action<string> appendMarker, CancellationToken ct)
    {
        List<PathwayTool> all = Reviewer.Pathways ?? new();
        // The writer has just finished: the time since this loop was set up is how long it took.
        int w = Steps.FindIndex(st => st.Kind == "write");
        if (w >= 0) Steps[w] = Steps[w] with { What = $"wrote the reply ({Words(currentReply())} words)", Seconds = Total.Elapsed.TotalSeconds };
        List<string> log = new();
        int fails = 0;
        for (int attempt = 1; attempt <= MaxRounds + 2 && !ct.IsCancellationRequested; attempt++)
        {
            bool outOfRounds = fails >= MaxRounds;
            List<PathwayTool> offered = outOfRounds && LoopPathway is not null
                ? all.Where(p => !p.Name.Equals(LoopPathway, StringComparison.OrdinalIgnoreCase)).Append(Settle).ToList()
                : all;
            string reply = currentReply();
            Stopwatch sw = Stopwatch.StartNew();
            (string name, Dictionary<string, string> args) = await Ask(reply, attempt, outOfRounds, offered, log, ct);
            string reason = args.GetValueOrDefault("reason") ?? args.Values.FirstOrDefault() ?? "";
            bool loops = LoopPathway is not null && name.Equals(LoopPathway, StringComparison.OrdinalIgnoreCase) && !outOfRounds;
            string call = args.Count == 0 ? $"{name}()" : $"{name}({string.Join(", ", args.Values.Select(v => $"\"{Short(v)}\""))})";
            Steps.Add(new Step(Reviewer.Name, call, sw.Elapsed.TotalSeconds, loops ? "fail" : name.Equals("Settle", StringComparison.OrdinalIgnoreCase) ? "settle" : "pass"));
            log.Add($"Attempt {attempt}: you called {call}.");
            Shared.Logger.LogInformation("[Review] ({Thread}) attempt {N}: {Call}", Chat.Key, attempt, call);
            if (!loops) break;

            fails++;
            Stopwatch rw = Stopwatch.StartNew();
            string revised = await Rewrite(reply, reason, ct);
            if (revised.Length == 0 || revised == reply) { Steps.Add(new Step(WriterLabel, "could not improve it", rw.Elapsed.TotalSeconds)); continue; }
            await apply(ReplyDiff.Edits(reply, revised));
            if (currentReply().Trim() != revised.Trim()) await apply(new() { new ReplyEdit(currentReply().Trim(), revised.Trim()) });
            Steps.Add(new Step(WriterLabel, $"rewrote ({Words(revised)} words) · round {fails + 1}", rw.Elapsed.TotalSeconds));
        }
        Steps.Add(new Step("END", "sent to you", Total.Elapsed.TotalSeconds, "end"));
        string json = JsonSerializer.Serialize(new { steps = Steps.Select(s => new { who = s.Who, what = s.What, s = Math.Round(s.Seconds, 1), kind = s.Kind }) });
        appendMarker("\n<!--ari-review:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "-->");
    }

    /// <summary>One question to the reviewer, for loops that run outside a reply's turn (the Code pipeline).</summary>
    internal Task<(string, Dictionary<string, string>)> AskOnce(string reply, int attempt, bool outOfRounds, CancellationToken ct)
    {
        List<PathwayTool> all = Reviewer.Pathways ?? new();
        List<PathwayTool> offered = outOfRounds && LoopPathway is not null
            ? all.Where(p => !p.Name.Equals(LoopPathway, StringComparison.OrdinalIgnoreCase)).Append(Settle).ToList() : all;
        return Ask(reply, attempt, outOfRounds, offered, new List<string>(), ct);
    }

    private string WriterLabel => Writer is Dialogue ? "Dialogue" : Writer.Name;

    /// <summary>Asks the reviewer. It may call any of the offered pathways; the first call wins. If it calls none it is asked once more, and
    /// failing that the first offered pathway is taken.</summary>
    private async Task<(string, Dictionary<string, string>)> Ask(string reply, int attempt, bool outOfRounds, List<PathwayTool> offered, List<string> log, CancellationToken ct)
    {
        (string Name, Dictionary<string, string> Args)? chosen = null;
        // Not read-only: the read-only filter would hide the pathway tools, which change nothing anyway; they are the only tools here.
        Thread scratch = new(ThreadPipeline.Dialogue, $"review:{Guid.NewGuid():N}") { Internal = true };
        foreach (PathwayTool p in offered)
        {
            PathwayTool tool = p;
            object schema = new
            {
                type = "function",
                function = new
                {
                    name = tool.Name, description = tool.Description,
                    parameters = new { type = "object", properties = tool.Args.ToDictionary(a => a, a => (object)new { type = "string" }), required = tool.Args.ToArray() },
                },
            };
            scratch.RegisterTool(tool.Name, schema, argsJson =>
            {
                Dictionary<string, string> args = new(StringComparer.OrdinalIgnoreCase);
                try { foreach (JsonProperty prop in JsonDocument.Parse(argsJson).RootElement.EnumerateObject()) args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString(); } catch { }
                chosen ??= (tool.Name, args);
                return Task.FromResult<ToolResult>("Recorded. You are done: write nothing more.");
            });
        }

        Dictionary<string, string> filled = ReviewerInputs();
        filled[ReviewerReplyPort] = reply;
        filled["message"] = Message;
        filled["attempt"] = attempt.ToString();
        filled["max_attempts"] = (MaxRounds + 1).ToString();
        filled["attempt_log"] = log.Count == 0 ? "(this is the first attempt)" : string.Join("\n", log);
        filled["seconds_since_reply"] = ((int)(DateTime.Now - UserSentAt).TotalSeconds).ToString();

        string userText = string.IsNullOrWhiteSpace(Reviewer.MessagePrompt) ? $"The reply to review:\n{reply}" : PipelineGraph.Fill(Reviewer.MessagePrompt, filled);
        StringBuilder added = new();
        added.AppendLine().AppendLine().AppendLine("— added by ARI —");
        added.AppendLine($"Attempt {attempt} of {MaxRounds + 1}. The user has been waiting {filled["seconds_since_reply"]} seconds.");
        if (log.Count > 0) added.AppendLine(string.Join("\n", log));
        if (outOfRounds) added.AppendLine("You are out of attempts: the reply can't be sent back again.");
        added.AppendLine("Finish by calling exactly one of your tools:");
        foreach (PathwayTool p in offered) added.AppendLine($"  {p.Signature}  {p.Description}");

        using IDisposable _ = CustomAgent.Resolve(PipelineGraph.Fill(Reviewer.AgentPrompt, filled));
        await Reviewer.Prompt(scratch, userText + added, new PromptOptions { Ct = ct, MaxTokensOverride = 800 });
        if (chosen is null)
        {
            string names = string.Join(", ", offered.Select(p => p.Signature));
            await Reviewer.Prompt(scratch, $"You didn't call a tool. Call exactly one of these now: {names}.", new PromptOptions { Ct = ct, MaxTokensOverride = 400 });
        }
        return chosen is { } c ? (c.Name, c.Args) : (offered[0].Name, new Dictionary<string, string>());
    }

    /// <summary>The writer's second go: the reply it wrote, the reviewer's reason (its critique input), and an instruction to change only that.</summary>
    private async Task<string> Rewrite(string reply, string reason, CancellationToken ct)
    {
        Thread scratch = new(ThreadPipeline.Dialogue, $"rewrite:{Guid.NewGuid():N}") { Internal = true, ReadOnlyTools = true };
        string text = $"The user said:\n{Message}\n\nYou replied:\n{reply}\n\nA reviewer read your reply and said:\n{reason}\n\n" +
                      "Write your reply again with that fixed. Change only what the review asks for and keep everything else word for word. Output only the reply.";
        Agent.PromptTokens.Value = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CritiquePort] = reason, ["memory"] = "", ["memories"] = "" };
        try
        {
            string result = await Writer.Prompt(scratch, text, new PromptOptions { Ct = ct, MaxTokensOverride = Math.Max(400, reply.Length) });
            return Regex.Replace(result, @"<!--ari-[^>]*-->", "").Trim();
        }
        finally { Agent.PromptTokens.Value = null; }
    }

    private static string Short(string s) => s.Length <= 80 ? s : s[..77] + "…";
    private static int Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>Turns a reply and its revised version into a few exact edits, so the change can be shown being made rather than the reply being replaced.</summary>
internal static class ReplyDiff
{
    private static readonly Regex Token = new(@"\s+|[^\s]+", RegexOptions.Compiled);

    internal static List<ReplyEdit> Edits(string before, string after)
    {
        before = before.Trim(); after = after.Trim();
        string[] a = Token.Matches(before).Select(m => m.Value).ToArray(), b = Token.Matches(after).Select(m => m.Value).ToArray();
        if (a.Length == 0 || a.Length > 1500 || b.Length > 1500) return new() { new ReplyEdit(before, after) };

        int[,] lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        if (lcs[0, 0] < a.Length * 0.4) return new() { new ReplyEdit(before, after) };   // mostly rewritten: one replacement reads better

        // Walk the alignment into hunks of [a range) -> [b range).
        List<(int A0, int A1, int B0, int B1)> hunks = new();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; continue; }
            int ax = x, by = y;
            while ((x < a.Length || y < b.Length) && !(x < a.Length && y < b.Length && a[x] == b[y]))
            {
                if (y >= b.Length || (x < a.Length && lcs[x + 1, y] >= lcs[x, y + 1])) x++; else y++;
            }
            hunks.Add((ax, x, by, y));
        }

        List<ReplyEdit> edits = new();
        foreach ((int a0, int a1, int b0, int b1) in hunks)
        {
            // Widen with unchanged words either side until the old text is there exactly once (an insertion needs a word to hang on).
            int l = a0, r = a1;
            string Old() => string.Concat(a[l..r]);
            for (int grow = 0; grow < 12 && (Old().Trim().Length == 0 || Count(before, Old()) != 1); grow++)
            {
                if (l > 0) l--;
                if (r < a.Length && (Old().Trim().Length == 0 || Count(before, Old()) != 1)) r++;
            }
            string oldText = Old();
            if (oldText.Length == 0 || Count(before, oldText) != 1) return new() { new ReplyEdit(before, after) };
            string newText = string.Concat(a[l..a0]) + string.Concat(b[b0..b1]) + string.Concat(a[a1..r]);
            edits.Add(new ReplyEdit(oldText, newText));
        }
        return edits.Count > 6 ? new() { new ReplyEdit(before, after) } : edits;
    }

    private static int Count(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += Math.Max(1, part.Length); }
        return n;
    }
}

/// <summary>
/// The Code pipeline's review loop. A reviewer wired to the Coder in the Code graph reads what changed (the diff), the coding conventions and
/// the Coder's report once a turn has changed files. Hard rules (CodeRules.json: patterns that must never appear in added lines) fail it
/// without asking anyone; otherwise the reviewer judges. Fail(reason) gives the Coder another turn with the reason, up to the wire's limit.
/// Every Fail is logged with the other corrections, so the idle suggestion pass can propose new conventions from them.
/// </summary>
internal static class CodeReview
{
    internal sealed record Rule(string Pattern, string Why);

    private static readonly Rule[] DefaultRules =
    [
        new(@"Co-Authored-By", "attribution lines are never added to code or commits"),
        new(@"Generated with \[?Claude", "attribution lines are never added to code or commits"),
        new(@"(?i)\b(api[_-]?key|secret|password|token)\b\s*[:=]\s*[""'][A-Za-z0-9_\-\.]{16,}[""']", "a secret is written into the code"),
    ];

    internal static List<Rule> Rules()
    {
        string path = Path.Combine(Paths.PersistentData, "CodeRules.json");
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(DefaultRules, new JsonSerializerOptions { WriteIndented = true }));
            return JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? DefaultRules.ToList();
        }
        catch { return DefaultRules.ToList(); }
    }

    /// <summary>The changes to the files the Coder touched this turn: a git diff where the project is a repository on this disk, else the list of files.</summary>
    internal static string Changes(string? root, IEnumerable<string> touched)
    {
        List<string> files = touched.Distinct().ToList();
        if (files.Count == 0) return "";
        if (root is { Length: > 0 } && Directory.Exists(Path.Combine(root, ".git")))
        {
            try
            {
                ProcessStartInfo psi = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
                foreach (string a in new[] { "diff", "--no-color", "-U2", "--" }.Concat(files)) psi.ArgumentList.Add(a);
                using Process p = Process.Start(psi)!;
                Task<string> output = p.StandardOutput.ReadToEndAsync();
                if (p.WaitForExit(20000) && output.Result.Trim().Length > 0)
                    return output.Result.Length > 14000 ? output.Result[..14000] + "\n… (diff cut short)" : output.Result;
            }
            catch { }
        }
        return "Files changed this turn:\n" + string.Join("\n", files.Select(f => "- " + f));
    }

    /// <summary>The first hard rule an added line breaks, as a reason, or null.</summary>
    internal static string? HardRuleBroken(string diff)
    {
        string[] added = diff.Split('\n').Where(l => l.StartsWith('+') && !l.StartsWith("+++")).ToArray();
        foreach (Rule r in Rules())
            foreach (string line in added)
                try { if (Regex.IsMatch(line, r.Pattern)) return $"Hard rule: {r.Why}. Line: {line.TrimStart('+').Trim()}"; } catch { }
        return null;
    }

    /// <summary>Runs the review after a Code turn that changed files. <paramref name="again"/> runs the Coder once more with the reviewer's reason and returns its report.</summary>
    internal static async Task Run(Thread thread, string message, string report, string? root, Func<List<string>> filesThisTurn, Func<string, Task<string>> again, Func<string, CustomAgent?> agentByName, CancellationToken ct)
    {
        PipelineGraph? graph = LLMModule.CodeGraph;
        PipelineGraph.Node? coder = graph?.Nodes.FirstOrDefault(n => n.Agent?.Equals("Coder", StringComparison.OrdinalIgnoreCase) == true);
        if (graph is null || coder is null) return;
        PipelineGraph.Node? revNode = graph.Nodes.FirstOrDefault(n => n.Type == "agent" && graph.Edges.Any(e => e.From == coder.Id && e.To == n.Id) && agentByName(n.Agent!)?.Pathways is { Count: > 0 });
        if (revNode is null || agentByName(revNode.Agent!) is not { } reviewer) return;
        PipelineGraph.Edge? back = graph.Edges.FirstOrDefault(e => e.From == revNode.Id && graph.IsPathway(e) && e.To == coder.Id);
        string replyPort = graph.Edges.First(e => e.From == coder.Id && e.To == revNode.Id).Port;
        string conventions = LLMModule.ConventionsProvider?.Invoke() ?? "";

        int fails = 0, max = back?.MaxRounds ?? 2;
        for (int round = 1; round <= max + 1 && !ct.IsCancellationRequested; round++)
        {
            string changes = Changes(root, filesThisTurn());
            if (changes.Length == 0) return;
            string? hard = HardRuleBroken(changes);
            string choice; string reason = "";
            if (hard is not null) { choice = back?.FromPort ?? "Fail"; reason = hard; }
            else
            {
                ReviewLoop ask = new()
                {
                    Chat = thread, Reviewer = reviewer, Writer = reviewer, ReviewerReplyPort = replyPort, Message = message, MaxRounds = max, LoopPathway = back?.FromPort,
                    ReviewerInputs = () => new(StringComparer.OrdinalIgnoreCase) { ["changes"] = changes, ["conventions"] = conventions },
                };
                (choice, Dictionary<string, string> args) = await ask.AskOnce(report, round, fails >= max, ct);
                reason = args.GetValueOrDefault("reason") ?? args.Values.FirstOrDefault() ?? "";
            }
            Shared.Logger.LogInformation("[CodeReview] ({Thread}) round {N}: {Choice} {Reason}", thread.Key, round, choice, reason);
            bool loops = back is not null && choice.Equals(back.FromPort, StringComparison.OrdinalIgnoreCase) && fails < max;
            if (!loops) return;
            Reflector.Record(thread.Key, ReflectMode.Work, [new ReplyEdit("code review", reason)]);
            fails++;
            report = await again($"[Code review] {reason}\nFix this in the code. If you think the review is wrong, say why instead of changing anything.");
        }
    }
}
