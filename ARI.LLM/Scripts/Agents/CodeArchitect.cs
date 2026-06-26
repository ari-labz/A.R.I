using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Planning half of the Code pipeline. Explores the codebase (read/search only — never edits) on an
/// internal sub-thread and decomposes a request into an ordered list of atomic steps, then commissions
/// a <see cref="Coder"/> for each. The whole flow renders as ONE continuous parent response — the user
/// never sees the architect/coder split, the plan JSON, or any "sub-thread" markup. Both the LLM planner
/// (its SystemPrompt) and the C# coordinator (its <see cref="Orchestrate"/> method) live here.
/// </summary>
internal sealed class CodeArchitect : Agent
{
    public CodeArchitect() { }

    // Coding prompts are verbose and already logged by the pipeline; don't double-log them.
    [JsonIgnore] internal override bool SuppressPromptLog => true;

    private sealed record CodePlan(List<string> Decisions, List<CodeStep> Steps);
    private sealed record CodeStep(string File, string Range, string Change);

    // ── Architect-driven loop: architect runs ON the main thread and approves each task ────────────
    /// <summary>
    /// The architect plans on the MAIN thread (its reasoning, tool calls and task list ARE the thread), then
    /// for each task commissions a Coder on a sub-thread whose work live-streams into the main thread. After
    /// each task the architect gets a lean SUMMARY (not the Coder's full trace), approves, and proceeds — and
    /// may inject a fix step. Every architect turn is a real main-thread turn, so the debug viewer shows the
    /// whole orchestration and we can confirm the approvals don't re-reason.
    /// </summary>
    internal async Task<string> RunLoop(
        Thread parent, string threadKey, string prompt, string username,
        Coder coder, string root, FileSnapshots snapshots, CancellationTokenSource cts, Func<string, Task>? onDelta)
    {
        // 1. PLAN — the architect's first turn, on the main thread: explore + emit the task list.
        new PreviewFile(root, cts.Token).Register(parent);
        new ReadFile(root, cts.Token).Register(parent);
        new ListDirectory(root, cts.Token).Register(parent);
        new SearchFiles(root, cts.Token).Register(parent);
        new FindFiles(root, cts.Token).Register(parent);

        string planText = await SendPrompt(parent, prompt, username, ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);
        CodePlan? plan = ParsePlan(planText);
        if (plan is null || plan.Steps.Count == 0)
        {
            Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) no task list — returning architect reply.", threadKey);
            return planText;   // architect answered or asked a question; nothing to execute
        }
        Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) plan: {N} task(s), {D} decision(s).", threadKey, plan.Steps.Count, plan.Decisions.Count);

        // Replace the plan response the USER sees with a clean, readable plan — strip the machine-readable JSON
        // and the architect's exploration chips (the full text stays in the debug trace).
        AriResponse? planResp = parent.History.OfType<AriResponse>().LastOrDefault();
        if (planResp is not null)
        {
            string chatPlan = PlanChatText(planText);
            if (chatPlan.Length == 0)
                chatPlan = "I'll make the following changes:\n" +
                    string.Join("\n", plan.Steps.Select((s, i) => $"{i + 1}. {s.Change}"));
            planResp.Content = AriContentBlock.Parse(chatPlan);
            parent.RaiseUpdated();
        }

        // 2. EXECUTE — one Coder sub-thread per task; feed each summary back for the architect to approve.
        //    The "[System] Task N…" feedback prompts and the PROCEED/DONE approvals are CHAT-HIDDEN internal
        //    orchestration (still in the debug view + the architect's context), so the user sees only:
        //    plan → each task's work → final summary.
        Queue<CodeStep> tasks = new(plan.Steps);
        int n = 0;
        while (tasks.Count > 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            CodeStep task = tasks.Dequeue();
            n++;

            string summary = await RunTask(parent, threadKey, n, task, plan, username, coder, root, snapshots, cts, onDelta);

            Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) task {N} done → architect approval (hidden).", threadKey, n);
            string approval = await SendPrompt(parent,
                $"[System] Task {n} is complete. Result reported by the Coder:\n{summary}\n\n" +
                "Do NOT re-plan or re-read the codebase — you already planned this. If the result looks correct, " +
                "reply with exactly: PROCEED. If it needs a fix or an extra step, give that step as a single ```json " +
                "step block. When the entire original request is finished, reply with exactly: DONE.",
                username, ct: cts.Token, userMessagePreadded: false, onDelta: null, chatHidden: true, thinkOverride: false);

            if (System.Text.RegularExpressions.Regex.IsMatch(approval, @"\bDONE\b", RegexOptions.IgnoreCase)) break;
            CodePlan? extra = ParsePlan(approval);   // architect may inject a fix / extra task
            if (extra is not null)
                foreach (CodeStep s in extra.Steps) tasks.Enqueue(s);
        }

        // 3. SUMMARISE — a final user-facing message (shown). The instruction is chat-hidden; the reply is shown.
        const string sumInstruction = "[System] All tasks are complete. Write a brief, friendly summary for the " +
            "user of what you changed across the codebase (a sentence or a short numbered list). Plain English " +
            "only — no JSON, no tool calls.";
        parent.History.Add(new UserMessage { Username = username, Content = sumInstruction, Timestamp = DateTime.Now, ChatHidden = true });
        string finalSummary = await SendPrompt(parent, sumInstruction, username, ct: cts.Token,
            userMessagePreadded: true, onDelta: onDelta, chatHidden: false, thinkOverride: false);
        return finalSummary;
    }

    /// <summary>The readable plan the user sees in chat — the architect's prose with the machine-readable JSON
    /// task list and the exploration tool-chips removed (those remain in the debug trace).</summary>
    private static string PlanChatText(string planText)
    {
        string s = StripPlanJson(planText);
        s = Regex.Replace(s, "<div class=\"tool-use\">[\\s\\S]*?</div>", "");
        s = Regex.Replace(s, @"<!--ari-[\s\S]*?-->", "");
        return s.Trim();
    }

    /// <summary>Runs one task on a Coder sub-thread, mirroring its live output into a UI-only main-thread
    /// response (the user watches it work), and returns a lean summary for the architect's context.</summary>
    private async Task<string> RunTask(
        Thread parent, string threadKey, int n, CodeStep task, CodePlan plan, string username,
        Coder coder, string root, FileSnapshots snapshots, CancellationTokenSource cts, Func<string, Task>? onDelta)
    {
        // UI-only mirror on the MAIN thread: shows the Coder's work live but never enters the architect's context.
        AriResponse mirror = new() { Timestamp = DateTime.Now, State = AriResponseState.Streaming, UiOnly = true };
        parent.History.Add(mirror);
        parent.streamingResponse = mirror;
        parent.State = ThreadState.Streaming;
        parent.RaiseUpdated();

        Thread child = new(ThreadPipeline.Code, $"{threadKey}#task{n}:{Guid.NewGuid():N}")
            { Internal = true, Parent = parent, Label = $"Task {n}: {task.Change}" };
        parent.AddChild(child);
        RegisterCoderTools(child, root, snapshots, cts.Token);

        string seed = ReadNumberedView(root, task.File, new List<CodeStep> { task });
        if (seed.Length > 0) child.PreReadPaths.Add(Path.GetFileName(task.File));

        async Task Push(string live)
        {
            mirror.StreamText = live;
            parent.RaiseStreaming(live);
            if (onDelta is not null) await onDelta(live);
        }

        await coder.SendPrompt(child, BuildFilePrompt(plan, task.File, new List<CodeStep> { task }, seed),
            username, ct: cts.Token, userMessagePreadded: false, onDelta: async t => await Push(t));

        AriResponse? coderResp = child.History.OfType<AriResponse>().LastOrDefault();
        string childContent = coderResp?.ContentText?.Trim() ?? "";

        mirror.Content           = AriContentBlock.Parse(childContent);
        mirror.StreamText        = null;
        mirror.State             = AriResponseState.Complete;
        parent.streamingResponse = null;
        parent.State             = ThreadState.Idle;
        parent.RaiseUpdated();

        // Lean summary for the architect = the Coder's own closing prose (its trace stays on the sub-thread).
        string prose = coderResp is null ? "" :
            string.Concat(coderResp.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
        return prose.Length > 0 ? prose : $"Edited {task.File} (task {n}).";
    }

    /// <summary>
    /// Plan→execute orchestration. Streams a single, JSON-free parent response: the architect's tool
    /// chips, then each Coder step's chips + result, inline — indistinguishable from a normal thread.
    /// </summary>
    internal async Task<string> Orchestrate(
        Thread                  parent,
        string                  threadKey,
        string                  prompt,
        string                  username,
        Coder                   coder,
        string                  root,
        FileSnapshots           snapshots,
        CancellationTokenSource cts,
        Func<string, Task>?     onDelta)
    {
        // One continuous parent response we drive by hand, so the live (polled) content shows only tool
        // chips and prose — never the plan JSON, even mid-stream.
        AriResponse resp = new() { Timestamp = DateTime.Now, State = AriResponseState.Streaming };
        parent.History.Add(resp);
        parent.streamingResponse = resp;
        parent.State             = ThreadState.Streaming;
        parent.RaiseUpdated();

        string baked = "";   // visible content locked in across phases (architect chips, then coder chips+prose)

        async Task Push(string live)
        {
            resp.StreamText = live;
            parent.RaiseStreaming(live);
            if (onDelta is not null) await onDelta(live);
        }

        try
        {
            // 1. PLAN — architect explores on an internal sub-thread. Only its tool chips reach the parent
            //    stream (prose/JSON filtered out), so no JSON ever appears, even while streaming.
            Thread planThread = new(ThreadPipeline.Code, $"{threadKey}#plan:{Guid.NewGuid():N}") { Internal = true, Parent = parent, Label = "Planning (architect)" };
            parent.AddChild(planThread);
            new PreviewFile(root, cts.Token).Register(planThread);
            new ReadFile(root, cts.Token).Register(planThread);
            new ListDirectory(root, cts.Token).Register(planThread);
            new SearchFiles(root, cts.Token).Register(planThread);
            new FindFiles(root, cts.Token).Register(planThread);

            string planText = await SendPrompt(planThread, prompt, username, ct: cts.Token, userMessagePreadded: false,
                onDelta: async t => await Push(baked + ChipsOnly(t)));

            AriResponse? planResp = planThread.History.OfType<AriResponse>().LastOrDefault();
            resp.ThinkingSeconds  = planResp?.ThinkingSeconds;
            baked                 = ChipsOnly(planResp?.ContentText ?? "");
            await Push(baked);

            CodePlan? plan = ParsePlan(planText);
            if (plan is null || plan.Steps.Count == 0)
            {
                // No actionable plan — the architect asked a question or hit a blocker. Show its prose
                // (JSON stripped) as the reply; nothing to execute.
                string msg = StripPlanJson(planResp?.ContentText ?? planText);
                Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) no executable plan — returning planner output.", threadKey);
                Finalize(resp, parent, msg.Length > 0 ? msg : baked);
                return msg;
            }

            Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) plan: {Steps} step(s), {Decisions} decision(s).",
                threadKey, plan.Steps.Count, plan.Decisions.Count);

            // 2. EXECUTE — one lean Coder per FILE, with all of that file's atomic steps batched into a
            //    single edit pass. Batching by file turns an N-call-site refactor into ~one pass per file
            //    (fewer round-trips) and lets the Coder apply every change in one edit_file `edits` array so
            //    line numbers never shift mid-file. The located range is seeded in so it needn't re-read.
            List<(string File, List<CodeStep> Steps)> groups = GroupByFile(plan.Steps);
            int gi = 0;
            foreach ((string file, List<CodeStep> steps) in groups)
            {
                cts.Token.ThrowIfCancellationRequested();
                gi++;

                string changeLabel = steps.Count == 1 ? steps[0].Change : $"{steps.Count} changes in {file}";
                Thread child = new(ThreadPipeline.Code, $"{threadKey}#file{gi}:{Guid.NewGuid():N}")
                    { Internal = true, Parent = parent, Label = $"Step {gi}: {changeLabel}" };
                parent.AddChild(child);
                RegisterCoderTools(child, root, snapshots, cts.Token);

                // Seed the located content (#3): give the Coder the file's current line-numbered text so it
                // edits straight away. Only marks the file "pre-read" (skipping its read-before-edit guard)
                // when the seed is complete enough to edit from; otherwise the Coder reads it itself.
                string seed = ReadNumberedView(root, file, steps);
                if (seed.Length > 0) child.PreReadPaths.Add(Path.GetFileName(file));

                Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) → Coder file {N}/{Total}: {File} ({Steps} change(s), seeded={Seed})",
                    threadKey, gi, groups.Count, file, steps.Count, seed.Length > 0);

                string prefix = baked.Length > 0 ? baked + "\n" : "";
                await coder.SendPrompt(child, BuildFilePrompt(plan, file, steps, seed), username, ct: cts.Token,
                    userMessagePreadded: false, onDelta: async t => await Push(prefix + t));

                string childContent = child.History.OfType<AriResponse>().LastOrDefault()?.ContentText?.Trim() ?? "";
                baked = prefix + childContent;
                await Push(baked);
            }

            // 3. VERIFY — build the touched project(s); on real compile errors, dispatch a fix Coder per file
            //    and rebuild once. Catches incomplete/broken edits the way Claude does (build → read errors →
            //    fix). Skips gracefully when the project can't build on this machine (e.g. WPF on macOS).
            baked = await BuildVerify(parent, threadKey, username, coder, root, snapshots, plan, cts, baked, Push);

            Finalize(resp, parent, baked);
            return baked;
        }
        catch
        {
            // Don't leave the thread mid-stream — finalise with whatever we have.
            Finalize(resp, parent, baked);
            throw;
        }
    }

    private static void Finalize(AriResponse resp, Thread parent, string content)
    {
        resp.Content             = AriContentBlock.Parse(content);
        resp.StreamText          = null;
        resp.State               = AriResponseState.Complete;
        parent.streamingResponse = null;
        parent.State             = ThreadState.Idle;
        parent.RaiseUpdated();
    }

    /// <summary>Keeps only the tool-use card markers from a rendered response, dropping all prose — so the
    /// architect's exploration shows as chips with no plan JSON or commentary leaking through.</summary>
    private static string ChipsOnly(string content)
        => string.Concat(AriContentBlock.Parse(content).Where(b => b is not TextBlock).Select(b => b.ToString()));

    // ── Plan parsing ────────────────────────────────────────────────────────────

    private static CodePlan? ParsePlan(string raw)
    {
        string? json = ExtractJsonObject(raw);
        if (json is null) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement rootEl = doc.RootElement;

            List<string> decisions = new();
            if (rootEl.TryGetProperty("decisions", out JsonElement dec) && dec.ValueKind == JsonValueKind.Array)
                decisions = dec.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                               .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            List<CodeStep> steps = new();
            if (rootEl.TryGetProperty("steps", out JsonElement st) && st.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in st.EnumerateArray())
                {
                    string file   = el.TryGetProperty("file",   out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
                    string range  = el.TryGetProperty("range",  out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                    string change = el.TryGetProperty("change", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                    if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                        steps.Add(new CodeStep(file, range, change));
                }
            }

            return steps.Count > 0 ? new CodePlan(decisions, steps) : null;
        }
        catch { return null; }
    }

    /// <summary>Pulls the plan object out of the planner's reply: the last ```json fenced block, else
    /// the first balanced {…} span.</summary>
    private static string? ExtractJsonObject(string raw)
    {
        MatchCollection fenced = Regex.Matches(raw, "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline);
        if (fenced.Count > 0) return fenced[^1].Groups[1].Value.Trim();

        int start = raw.IndexOf('{');
        int end   = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw.Substring(start, end - start + 1) : null;
    }

    /// <summary>Removes the architect's plan JSON from displayed prose (fallback / no-plan path).</summary>
    private static string StripPlanJson(string content)
    {
        content = Regex.Replace(content, "```json\\s*\\{.*?\\}\\s*```", "", RegexOptions.Singleline);
        int d = content.IndexOf("\"decisions\"", StringComparison.Ordinal);
        if (d >= 0)
        {
            int brace = content.LastIndexOf('{', d);
            if (brace >= 0) content = content[..brace];
        }
        return content.Trim();
    }

    // ── Execution: file batching, content seeding, build verify ───────────────────

    private static void RegisterCoderTools(Thread child, string root, FileSnapshots snapshots, CancellationToken ct)
    {
        // Lean executor toolset: read the assigned range, edit, recover. Deliberately NO search/preview/list —
        // the architect already located the change; with thinking off, exploration tools just make the Coder
        // wander and spiral on search instead of editing.
        new ReadFile(root, ct).Register(child);
        new EditFile(root, ct, snapshots).Register(child);
        new WriteFile(root, ct).Register(child);
        new RevertFile(root, ct, snapshots).Register(child);
        new DeleteFile(root, ct).Register(child);
        new MoveFile(root, ct).Register(child);
    }

    /// <summary>Groups plan steps by file, preserving first-appearance order so dependency ordering between
    /// files is kept while every step for one file is executed together in a single Coder pass.</summary>
    private static List<(string File, List<CodeStep> Steps)> GroupByFile(List<CodeStep> steps)
    {
        List<(string File, List<CodeStep> Steps)> groups = new();
        Dictionary<string, int> idx = new(StringComparer.OrdinalIgnoreCase);
        foreach (CodeStep s in steps)
        {
            if (idx.TryGetValue(s.File, out int g)) groups[g].Steps.Add(s);
            else { idx[s.File] = groups.Count; groups.Add((s.File, new List<CodeStep> { s })); }
        }
        return groups;
    }

    private static (int Start, int End)? ParseRange(string range, int total)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        MatchCollection m = Regex.Matches(range, @"\d+");
        if (m.Count == 0) return null;
        int a = int.Parse(m[0].Value);
        int b = m.Count > 1 ? int.Parse(m[^1].Value) : a;
        a = Math.Clamp(a, 1, Math.Max(1, total));
        b = Math.Clamp(b, a, Math.Max(1, total));
        return (a, b);
    }

    // Files at/under this many lines are seeded whole (gives the Coder every line number to edit from). Larger
    // files are seeded as windows around the step ranges; if any step lacks a range, the seed is skipped and
    // the Coder reads the file itself (so it never edits a large file blind).
    private const int WHOLE_FILE_SEED_LINES = 200;
    private const int SEED_PAD              = 8;

    /// <summary>Builds a line-numbered view of <paramref name="file"/> to seed a Coder with the located
    /// content. Whole file when small; merged windows around the step ranges when large; empty string when a
    /// large file has a step with no usable range (caller then lets the Coder read it).</summary>
    private static string ReadNumberedView(string root, string file, List<CodeStep> steps)
    {
        string abs;
        try { abs = Path.GetFullPath(Path.Combine(root, file)); }
        catch { return ""; }
        if (!File.Exists(abs)) return "";   // new file (write) — nothing to seed
        string[] lines;
        try { lines = File.ReadAllLines(abs); }
        catch { return ""; }
        int total = lines.Length;
        if (total == 0) return "";

        List<(int Start, int End)> windows = new();
        if (total <= WHOLE_FILE_SEED_LINES)
        {
            windows.Add((1, total));
        }
        else
        {
            List<(int Start, int End)> ranges = new();
            foreach (CodeStep s in steps)
            {
                (int Start, int End)? r = ParseRange(s.Range, total);
                if (r is null) return "";   // large file + unlocated step → let the Coder read it
                ranges.Add((Math.Max(1, r.Value.Start - SEED_PAD), Math.Min(total, r.Value.End + SEED_PAD)));
            }
            if (ranges.Count == 0) return "";
            ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
            foreach ((int s, int e) in ranges)
            {
                if (windows.Count > 0 && s <= windows[^1].End + 2)
                    windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, e));
                else windows.Add((s, e));
            }
        }

        StringBuilder sb = new();
        for (int w = 0; w < windows.Count; w++)
        {
            (int s, int e) = windows[w];
            if (windows.Count > 1) sb.AppendLine($"… (lines {s}–{e}) …");
            for (int i = s; i <= e; i++) sb.AppendLine($"{i,6}: {lines[i - 1]}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildFilePrompt(CodePlan plan, string file, List<CodeStep> steps, string seed)
    {
        StringBuilder sb = new();
        if (plan.Decisions.Count > 0)
        {
            sb.AppendLine("Decisions for this task (keep these consistent):");
            foreach (string d in plan.Decisions) sb.AppendLine($"- {d}");
            sb.AppendLine();
        }

        if (steps.Count == 1)
        {
            sb.Append($"Make this change in `{file}`");
            if (!string.IsNullOrWhiteSpace(steps[0].Range)) sb.Append($" (around lines {steps[0].Range})");
            sb.AppendLine(":");
            sb.AppendLine(steps[0].Change);
        }
        else
        {
            sb.AppendLine($"Make these {steps.Count} changes in `{file}` — one file, one edit pass:");
            int i = 0;
            foreach (CodeStep s in steps)
            {
                i++;
                string loc = string.IsNullOrWhiteSpace(s.Range) ? "" : $" (around lines {s.Range})";
                sb.AppendLine($"{i}. {s.Change}{loc}");
            }
        }
        sb.AppendLine();

        if (seed.Length > 0)
        {
            sb.AppendLine($"Current content of `{file}` — these are the live line numbers; you already have it, do NOT read it again:");
            sb.AppendLine("```");
            sb.AppendLine(seed);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine(steps.Count > 1
                ? "Apply ALL of the changes in ONE edit_file call using the `edits` array, so the line numbers above stay valid for every item. Then stop. Do not change anything else."
                : "Make the edit with a single edit_file call against the line numbers above, then stop. Do not change anything else.");
        }
        else
        {
            sb.AppendLine("Read the relevant range, make the edit(s), then stop. Do not change anything else.");
        }
        return sb.ToString();
    }

    // ── Build verification (Claude-style build → read errors → fix) ───────────────

    /// <summary>After all edits, builds the touched project(s) and, on real compile errors, dispatches a fix
    /// Coder per affected file and rebuilds once. Returns the updated visible content. No-ops cleanly when the
    /// project can't be built here (e.g. a WPF project on macOS) so it never blocks or fabricates a result.</summary>
    private async Task<string> BuildVerify(Thread parent, string threadKey, string username, Coder coder,
        string root, FileSnapshots snapshots, CodePlan plan, CancellationTokenSource cts, string baked, Func<string, Task> push)
    {
        List<string> editedAbs = plan.Steps
            .Select(s => { try { return Path.GetFullPath(Path.Combine(root, s.File)); } catch { return ""; } })
            .Where(p => p.Length > 0 && File.Exists(p) && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (editedAbs.Count == 0) return baked;

        List<string> projects = editedAbs.Select(f => NearestProject(f, root)).OfType<string>()
                                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count == 0) return baked;   // not a dotnet project we can build — skip verification

        for (int round = 0; round < 2; round++)
        {
            cts.Token.ThrowIfCancellationRequested();
            await push(baked + (baked.Length > 0 ? "\n\n" : "") + "_Building…_");

            Dictionary<string, List<string>> errors = new(StringComparer.OrdinalIgnoreCase);
            bool anyRan = false;
            foreach (string proj in projects)
            {
                (bool ran, string output) = await DotnetBuild(proj, cts.Token);
                if (!ran) continue;
                anyRan = true;
                foreach (KeyValuePair<string, List<string>> kv in CollectCsErrors(output, root))
                {
                    if (!errors.TryGetValue(kv.Key, out List<string>? l)) errors[kv.Key] = l = new();
                    foreach (string e in kv.Value) if (!l.Contains(e)) l.Add(e);
                }
            }

            if (!anyRan) return baked;   // couldn't build at all (OS/tooling) — don't claim a verdict

            if (errors.Count == 0)
            {
                Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) build verify clean ({N} project(s)).", threadKey, projects.Count);
                baked += (baked.Length > 0 ? "\n\n" : "") + "**Build:** ✓ compiles cleanly.";
                await push(baked);
                return baked;
            }

            if (round == 1)
            {
                // Already ran a fix round; this rebuild still fails. Stop rather than loop.
                Shared.Logger.LogWarning("[CodeArchitect] ({Thread}) build still failing after fix round ({N} file(s)).", threadKey, errors.Count);
                baked += (baked.Length > 0 ? "\n\n" : "") + $"**Build:** still failing in {errors.Count} file(s) after a fix attempt — left for review.";
                await push(baked);
                return baked;
            }

            int totalErr = errors.Sum(e => e.Value.Count);
            Shared.Logger.LogWarning("[CodeArchitect] ({Thread}) build verify: {E} error(s) across {F} file(s) — dispatching fixes.", threadKey, totalErr, errors.Count);
            baked += (baked.Length > 0 ? "\n\n" : "") + $"**Build:** {totalErr} error(s) — fixing.";
            await push(baked);

            foreach ((string relFile, List<string> errList) in errors)
            {
                cts.Token.ThrowIfCancellationRequested();
                Thread fix = new(ThreadPipeline.Code, $"{threadKey}#fix:{Guid.NewGuid():N}")
                    { Internal = true, Parent = parent, Label = $"Fix build errors in {relFile}" };
                parent.AddChild(fix);
                RegisterCoderTools(fix, root, snapshots, cts.Token);

                // Seed windows around the error lines so a large file isn't dumped whole.
                List<CodeStep> errSteps = errList
                    .Select(e => Regex.Match(e, @"line\s+(\d+)"))
                    .Where(m => m.Success).Select(m => new CodeStep(relFile, m.Groups[1].Value, "")).ToList();
                string seed = ReadNumberedView(root, relFile, errSteps);
                if (seed.Length > 0) fix.PreReadPaths.Add(Path.GetFileName(relFile));

                string prefix = baked.Length > 0 ? baked + "\n" : "";
                await coder.SendPrompt(fix, BuildFixPrompt(relFile, errList, seed), username, ct: cts.Token,
                    userMessagePreadded: false, onDelta: async t => await push(prefix + t));

                string fc = fix.History.OfType<AriResponse>().LastOrDefault()?.ContentText?.Trim() ?? "";
                baked = prefix + fc;
                await push(baked);
            }
        }
        return baked;
    }

    private static string BuildFixPrompt(string file, List<string> errors, string seed)
    {
        StringBuilder sb = new();
        sb.AppendLine($"The project failed to compile after edits to `{file}`. Fix ONLY these compiler errors, with the smallest possible edits — do not refactor or touch anything unrelated:");
        foreach (string e in errors) sb.AppendLine($"- {e}");
        sb.AppendLine();
        if (seed.Length > 0)
        {
            sb.AppendLine($"Current content of `{file}` — live line numbers, you already have it:");
            sb.AppendLine("```");
            sb.AppendLine(seed);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Make the fix with edit_file against the line numbers above, then stop. If the file is badly broken, use revert_file to restore it and redo the change cleanly.");
        }
        else
        {
            sb.AppendLine("Read the lines named in the errors, fix them, then stop.");
        }
        return sb.ToString();
    }

    /// <summary>Nearest .csproj walking up from a file, bounded by the project root.</summary>
    private static string? NearestProject(string absFile, string root)
    {
        string rootFull;
        try { rootFull = Path.GetFullPath(root); } catch { return null; }
        string? dir = Path.GetDirectoryName(absFile);
        while (dir is not null && dir.StartsWith(rootFull, StringComparison.Ordinal))
        {
            string[] projs;
            try { projs = Directory.GetFiles(dir, "*.csproj"); } catch { return null; }
            if (projs.Length > 0) return projs[0];
            if (string.Equals(dir, rootFull, StringComparison.Ordinal)) break;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static async Task<(bool Ran, string Output)> DotnetBuild(string csproj, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName               = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                WorkingDirectory       = Path.GetDirectoryName(csproj) ?? "."
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csproj);
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("-clp:ErrorsOnly");
            // Harmless for cross-platform projects; lets Windows-targeted ones get far enough to surface real
            // C# errors on macOS/Linux instead of failing immediately on the framework reference.
            psi.ArgumentList.Add("-p:EnableWindowsTargeting=true");

            using Process proc = new() { StartInfo = psi };
            StringBuilder buf = new();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (buf) buf.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (buf) buf.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(180));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) throw;
                return (false, "build timed out");
            }
            lock (buf) return (true, buf.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, $"build could not run: {ex.Message}"); }
    }

    /// <summary>Pulls C# compile errors (CS####) out of dotnet build output, keyed by file path relative to
    /// the project root. Ignores NETSDK/MSB/XAML/framework errors so an unbuildable-on-this-OS project (WPF)
    /// doesn't masquerade as a code error.</summary>
    private static Dictionary<string, List<string>> CollectCsErrors(string output, string root)
    {
        Dictionary<string, List<string>> byFile = new(StringComparer.OrdinalIgnoreCase);
        string rootFull;
        try { rootFull = Path.GetFullPath(root); } catch { rootFull = root; }
        foreach (Match m in Regex.Matches(output,
                     @"(?im)^(?<path>[^(\r\n]+\.cs)\((?<line>\d+),\d+\):\s*error\s+(?<code>CS\d+):\s*(?<msg>.*?)(?:\s*\[[^\]]*\])?$"))
        {
            string path = m.Groups["path"].Value.Trim();
            string rel  = path;
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(rootFull, StringComparison.Ordinal)) rel = Path.GetRelativePath(rootFull, full);
            }
            catch { /* keep raw path */ }
            string line = $"line {m.Groups["line"].Value}: error {m.Groups["code"].Value}: {m.Groups["msg"].Value.Trim()}";
            if (!byFile.TryGetValue(rel, out List<string>? list)) byFile[rel] = list = new();
            if (!list.Contains(line)) list.Add(line);
        }
        return byFile;
    }
}
