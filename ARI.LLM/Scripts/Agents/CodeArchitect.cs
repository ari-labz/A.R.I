using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// The coding "architect": a conversational agent that runs ON the main thread with exploration tools
/// (read/search only) plus two execution tools — spawn_coder (dispatch one atomic edit to a <see cref="Coder"/>
/// sub-agent) and build_project (verify the edits compile). It infers whether a request needs a plan
/// (refactor / cross-file → present a plain-English plan and wait for approval) or can just be done
/// (a rename → execute directly), then drives its own tool loop: spawn coders one at a time (only proceeding
/// when the last succeeded), build, fix its own compile errors, and summarise. The LLM side is its
/// SystemPrompt; the C# side is <see cref="RunLoop"/>.
/// </summary>
internal sealed class CodeArchitect : Agent
{
    public CodeArchitect() { }

    // Coding prompts are verbose and already logged by the pipeline; don't double-log them.
    [JsonIgnore] internal override bool SuppressPromptLog => true;

    /// <summary>Grades how much thinking the plan turn needs (set by LLMModule). Null = no appraisal → no thinking.</summary>
    [JsonIgnore] internal Appraisal? Appraisal { get; set; }

    private sealed record CodePlan(List<string> Decisions, List<CodeStep> Steps);
    private sealed record CodeStep(string File, string Range, string Change);

    // Entry for every user turn on a Code thread (from CodePipeline). Registers the tools and runs one architect
    // turn; parent.AwaitingPlanApproval carries the "a plan is on the table, awaiting the user" state across turns
    // (set when a turn ends without spawning any coder). The plan prose itself lives in History — nothing is stashed.
    internal async Task<string> RunLoop(
        Thread parent, string threadKey, string prompt, string username,
        Coder coder, string root, FileSnapshots snapshots, CancellationTokenSource cts, Func<string, Task>? onDelta,
        bool remote = false)
    {
        // The architect is a conversational coding agent driving its OWN tool loop: exploration (read-only) plus
        // two execution tools — spawn_coder (dispatch one edit) and build_project (verify). It infers whether a
        // request needs a plan (refactor/cross-file → present + wait) or can just be done (rename → execute).
        // Remote projects: the client's forwarded read/preview/list/search/find tools are ALREADY on `parent`
        // (they run on the client's machine) — reuse them. Local projects: bind ServerFileSystem to this disk.
        if (!remote)
        {
            ServerFileSystem fs = new(root, cts.Token, snapshots);
            new PreviewFile(fs).Register(parent);
            new ReadFile(fs).Register(parent);
            new ListDirectory(fs).Register(parent);
            new SearchFiles(fs).Register(parent);
            new FindFiles(fs).Register(parent);
        }

        // Files changed this request — the success/gating signal (was a file modified?) and the build-error owner tag.
        HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
        int taskNum = 0;

        // Deterministic edit freeze: when the user explicitly forbids changes this turn ("planning only",
        // "do not edit"), spawn_coder is refused at the tool layer — a nudge alone cannot be trusted to
        // survive an approval-shaped injection (this exact failure corrupted a client file: the harness told
        // the model the plan was approved while the user's message said the opposite).
        bool editsForbidden = UserForbadeEdits(prompt);

        // spawn_coder(file, change, range?) — dispatch ONE edit to a Coder. Flat args (like read_file) so the
        // text-tool protocol carries it reliably; the model makes many small calls, never one nested submit.
        parent.RegisterTool("spawn_coder", SpawnCoderSchema, async argsJson =>
        {
            if (editsForbidden)
                return "[System: the user has explicitly forbidden edits this turn (planning only). spawn_coder is " +
                       "disabled. Present your plan as text and STOP — do not attempt further tool calls to edit.]";
            (string? file, string? change, string? range, string? err) = ParseCoderArgs(argsJson);
            if (err is not null) return err;
            taskNum++;
            // Local: track the absolute path for the build's yours-vs-pre-existing tagging. Remote: track the
            // repo-relative path (the build runs on the client; there's no server-side absolute path).
            string? abs = remote ? file : SafeAbs(root, file!);
            if (abs is not null) touched.Add(abs);
            (string summary, bool modified) = await RunOneCoder(
                parent, threadKey, taskNum, new CodeStep(file!, range ?? "", change!), username, coder, root, snapshots, cts, onDelta, remote);
            return modified
                ? $"Coder finished task {taskNum} on {file}. Result: {summary}"
                : $"[System: the Coder made NO change to {file} — this task likely FAILED. Result: {summary}. " +
                  "Do NOT spawn the next task; re-spawn this one with a clearer instruction, or stop and tell the user.]";
        },
        // No pre-card: RunOneCoder drops a <!--ari-subthread--> anchor into the stream itself (with the child
        // key it mints), and the child renders inline under its own labelled frame.
        displayFormatter: _ => "");

        // build_project() — build the touched project(s); errors grouped by file, tagged yours vs pre-existing.
        // Remote: the build runs on the client via its forwarded run_command (there's no dotnet on this server
        // for the client's project); local: dotnet build on this disk.
        parent.RegisterTool("build_project", BuildProjectSchema,
            async _ => remote ? await BuildRemote(parent, touched, cts.Token) : await BuildTouched(touched, root, cts.Token),
            displayFormatter: _ => "<!--ari-tool-start:build_project:project-->");

        bool bypass   = Environment.GetEnvironmentVariable("ARI_GATE_BYPASS") == "1";
        bool resuming = parent.AwaitingPlanApproval && !bypass;

        // Per-turn nudge (the full workflow lives in the architect's system prompt). The whole turn — plan, the
        // spawn_coder edits, the build_project check, and the summary — is ONE response made of many blocks.
        // The resuming nudge must NOT presume approval: the previous turn may have ended in a clarifying
        // question, and the user's reply may be a new or revised request rather than a go-ahead.
        string nudge = resuming
            ? "Your previous turn ended awaiting the user (a plan or a question). Read their reply carefully — it is " +
              "an approval ONLY if it clearly tells you to proceed with the plan you already presented. If it approves, " +
              "EXECUTE that plan now with spawn_coder (do not re-plan or re-explore). If it answers your question, adds " +
              "requirements, or changes the request, treat it as a new/revised request: present the updated numbered " +
              "plan and STOP for approval. If it forbids changes (e.g. 'planning only', 'do not edit'), you MUST NOT " +
              "call spawn_coder this turn — explore and plan only."
            : bypass
                ? "Automated run: write your numbered plan FIRST (before any tool call), then execute it directly with spawn_coder — no approval needed."
                : "Write your numbered plan to the user FIRST (before any spawn_coder call). Then: a small, localized change " +
                  "(a rename, a one-line fix) — proceed and spawn_coder now; a larger refactor / cross-file / multi-step / " +
                  "ambiguous change — STOP after the plan for the user to approve before you spawn any coder.";
        if (editsForbidden)
            nudge += " [The user has forbidden edits this turn — spawn_coder is disabled; plan only.]";

        // Every user prompt gets appraised — a "resume" can be anything from a one-word approval (grade 0)
        // to a fully-specified new request (the turn that actually needs a thinking budget).
        (int? grade, _, _) = await AppraiseThinking(prompt, threadKey, cts.Token);

        string reply = await SendPrompt(parent, prompt, username,
            augmentedPrompt: $"{prompt}\n\n[System: {nudge}]",
            ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);

        // Attach appraisal telemetry to this turn's response so the DTI can show it (chat view ignores it).
        if (grade is not null && parent.History.OfType<Response>().LastOrDefault() is { } appraised)
            appraised.AppraisalGrade = grade;

        // If it spawned coders it executed → done; otherwise it presented a plan / asked a question → await the user.
        parent.AwaitingPlanApproval = !bypass && touched.Count == 0;
        return reply;
    }

    // ── spawn_coder / build_project tools ─────────────────────────────────────────

    private static readonly object SpawnCoderSchema = new
    {
        type = "function",
        function = new
        {
            name = "spawn_coder",
            description = "Dispatch ONE atomic edit to a Coder sub-agent: one change to one file. Call it once per " +
                          "task, in dependency order — spawn the next only after the previous succeeded. Flat args: " +
                          "the file, a one-sentence change instruction, and (if known) the located line range.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    file   = new { type = "string", description = "Repo-relative path of the file to edit." },
                    change = new { type = "string", description = "One-sentence instruction describing the single change." },
                    range  = new { type = "string", description = "Located line range, e.g. \"120-128\" (optional; helps the Coder edit without re-reading)." }
                },
                required = new[] { "file", "change" }
            }
        }
    };

    private static readonly object BuildProjectSchema = new
    {
        type = "function",
        function = new
        {
            name = "build_project",
            description = "Build the project(s) containing the files you changed, to verify the edits compile. Call " +
                          "this once all edits are done. Returns compile errors grouped by file, tagged as ones you " +
                          "edited (fix them) or pre-existing (leave them, note them in your summary).",
            parameters = new { type = "object", properties = new { } }
        }
    };

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>True when the user's message explicitly forbids making changes this turn ("planning only",
    /// "do not edit any files", "don't make changes without my permission"). Deliberately conservative:
    /// only unambiguous no-edit phrasings trigger the freeze — a false positive just means a plan-only turn.</summary>
    internal static bool UserForbadeEdits(string prompt) => Regex.IsMatch(prompt, string.Join("|",
        @"\bplanning (phase|only|stage)\b",
        @"\b(just|only) (a )?plan\b",
        @"\bplan (it |this )?(out |first )?only\b",
        @"\bdo ?n[o']t (make|apply|do) (any )?(changes|edits|modifications)\b",
        @"\bdo not (make|apply|do) (any )?(changes|edits|modifications)\b",
        @"\bdo ?n[o']t (edit|change|modify|touch) (any|the|my)? ?(files?|code)\b",
        @"\bdo not (edit|change|modify|touch) (any|the|my)? ?(files?|code)\b",
        @"\bno (changes|edits) (yet|for now)\b",
        @"\bwithout my (explicit )?(permission|approval|say.?so|go.?ahead)\b"),
        RegexOptions.IgnoreCase);

    private static string? SafeAbs(string root, string rel)
    {
        try { return Path.GetFullPath(Path.Combine(root, rel)); } catch { return null; }
    }

    private static string SafeRead(string abs) { try { return File.ReadAllText(abs); } catch { return ""; } }

    private static (string? File, string? Change, string? Range, string? Error) ParseCoderArgs(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement r = doc.RootElement;
            string? file   = r.TryGetProperty("file",   out JsonElement f) ? f.GetString() : null;
            string? change = r.TryGetProperty("change", out JsonElement c) ? c.GetString() : null;
            string? range  = r.TryGetProperty("range",  out JsonElement g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change))
                return (null, null, null, "[System: spawn_coder needs both 'file' and 'change'.]");
            return (file!.Trim(), change!.Trim(), range?.Trim(), null);
        }
        catch { return (null, null, null, "[System: spawn_coder arguments were not valid JSON — provide 'file' and 'change'.]"); }
    }

    /// <summary>Runs ONE Coder on its OWN sub-thread for a single edit. The Coder streams into that child thread
    /// (its edit cards + result live in the child's history); the architect's response only holds a
    /// <see cref="Subthread"/> anchor pointing at the child, so the child renders inline for display yet never
    /// enters the architect's context. Returns the lean result summary and whether the target file actually
    /// changed (the success signal the architect gates on).</summary>
    private async Task<(string Summary, bool Modified)> RunOneCoder(
        Thread parent, string threadKey, int n, CodeStep task, string username,
        Coder coder, string root, FileSnapshots snapshots, CancellationTokenSource cts, Func<string, Task>? onDelta,
        bool remote = false)
    {
        string? abs    = remote ? null : SafeAbs(root, task.File);
        string  before = abs is not null && File.Exists(abs) ? SafeRead(abs) : "";

        Thread child = new(ThreadPipeline.Code, $"{threadKey}#coder{n}:{Guid.NewGuid():N}")
            { Internal = true, Parent = parent, Label = $"Coder {n}: {task.Change}" };
        parent.AddChild(child);
        // Remote: give the child the client's forwarded edit tools (they run on the client). Local: ServerFileSystem.
        if (remote) CopyForwardedCoderTools(parent, child);
        else        RegisterCoderTools(child, root, snapshots, cts.Token);

        // Anchor the child in the architect's CURRENT response: register it for display resolution, then drop a
        // subthread marker into the stream at this position (via the tool-display sink active during Execute).
        // The child's blocks splice in here for display; the architect's context sees only the summary we return.
        parent.streamingResponse?.Subthreads.TryAdd(child.Key, child);
        if (parent.ToolDisplaySink is not null)
            await parent.ToolDisplaySink($"<!--ari-subthread:{child.Key}|{Esc(task.Change)}-->");

        // Remote: no local disk to seed from — the Coder reads its file via the forwarded read_file. Local: seed
        // the located line-numbered view so the Coder edits without re-reading.
        string seed = remote ? "" : ReadNumberedView(root, task.File, new List<CodeStep> { task });
        if (seed.Length > 0) child.PreReadPaths.Add(Path.GetFileName(task.File));

        // The Coder streams into its OWN thread; poke the parent so watching clients re-poll and pick up the
        // child's freshly-streamed blocks through the resolved anchor (no flattening into the parent stream).
        async Task Live(string _)
        {
            parent.RaiseStreaming(parent.streamingResponse?.StreamText ?? "");
            if (onDelta is not null) await onDelta(parent.streamingResponse?.StreamText ?? "");
        }

        CodePlan lone = new(new List<string>(), new List<CodeStep> { task });
        await coder.SendPrompt(child, BuildFilePrompt(lone, task.File, new List<CodeStep> { task }, seed),
            username, ct: cts.Token, userMessagePreadded: false, onDelta: Live);

        Response? resp  = child.History.OfType<Response>().LastOrDefault();
        string    prose = resp is null ? "" : CleanCoderProse(string.Concat(resp.Content.OfType<TextBlock>().Select(b => b.Text)));

        // Modification signal: local diffs the file on disk; remote can't (file is on the client) so it checks
        // whether the Coder actually landed a successful edit_file/write_file/move/delete over the socket.
        bool modified = remote ? ChildLandedEdit(child) : before != (abs is not null && File.Exists(abs) ? SafeRead(abs) : "");

        // Honesty backstop: a Coder that leaves its LAST edit attempt blocked/failed may have left the file
        // broken and then talked itself into reporting success. Surface that to the architect explicitly so it
        // verifies instead of trusting the prose (this exact failure shipped a corrupted file as "complete").
        string summary = prose.Length > 0 ? prose : "(no summary reported)";
        if (modified && ChildEditTrouble(child))
            summary += "\n[System: WARNING — this Coder's last edit attempt on the file was blocked or failed after " +
                       "an earlier edit landed. The file may be in a broken or partial state regardless of what the " +
                       "summary above claims. Verify it now with preview_file and a ranged read_file before " +
                       "continuing; if it is broken, spawn a coder to repair or revert it.]";
        return (summary, modified);
    }

    // Strips UI display artefacts out of a Coder's prose before it enters the architect's context: the child's
    // stream text embeds tool-use cards (<div class="tool-use">…</div>) and marker comments (<!--ari-…-->)
    // which are display-only noise to another model.
    private static string CleanCoderProse(string prose)
    {
        prose = Regex.Replace(prose, "<div class=\"tool-use\">.*?</div>", "", RegexOptions.Singleline);
        prose = Regex.Replace(prose, "<!--ari-.*?-->", "", RegexOptions.Singleline);
        return prose.Trim();
    }

    /// <summary>True when the child's trace shows its final mutating attempt (edit/write/revert) was blocked or
    /// errored — i.e. an edit landed earlier but the Coder was subsequently refused mid-repair. That pattern means
    /// the file's state is unverified and possibly broken, whatever the Coder's summary says.</summary>
    private static bool ChildEditTrouble(Thread child)
    {
        bool lastMutatingFailed = false;
        foreach (Response r in child.History.OfType<Response>())
            foreach (TraceStep s in r.Trace ?? Enumerable.Empty<TraceStep>())
            {
                if (s.Kind != "tool_result" || s.Name is not ("edit_file" or "write_file" or "revert_file" or "move_file" or "delete_file")) continue;
                string t = (s.Text ?? "").TrimStart();
                lastMutatingFailed = t.StartsWith("[Blocked") || t.StartsWith("[Error") || t.StartsWith("[System") || t.StartsWith("Error");
            }
        return lastMutatingFailed;
    }

    // The forwarded edit tools a remote Coder needs — mirrors RegisterCoderTools' lean set (search/find to locate a
    // referenced symbol, but no list_directory, which makes a think-off Coder wander). Preferred path: the client
    // WebSocket layer's cloner, which registers a FRESH
    // guardrail scope for the child (its own read-dedup/preview/dirty state — a sub-agent must never inherit the
    // parent's "already read" ledger, since its context starts empty). Fallback: copy the parent's delegates
    // (shared state — pre-cloner behaviour) so a Coder still works if the cloner isn't wired.
    private static void CopyForwardedCoderTools(Thread from, Thread to)
    {
        if (from.ClientToolCloner is not null && from.ClientToolCloner(to)) return;
        foreach (string name in new[] { "preview_file", "read_file", "search_files", "find_files", "edit_file", "write_file", "delete_file", "move_file", "revert_file" })
            if (from.tools.TryGetValue(name, out var t)) to.tools[name] = t;
    }

    // True if the child issued a successful mutating tool call (its result isn't an error/refusal marker).
    private static bool ChildLandedEdit(Thread child)
    {
        foreach (Response r in child.History.OfType<Response>())
            foreach (TraceStep s in r.Trace ?? Enumerable.Empty<TraceStep>())
            {
                if (s.Kind != "tool_result" || s.Name is not ("edit_file" or "write_file" or "move_file" or "delete_file")) continue;
                string t = (s.Text ?? "").TrimStart();
                if (!t.StartsWith("[Error") && !t.StartsWith("[System") && !t.StartsWith("SAFETY MODE") && !t.StartsWith("Error"))
                    return true;
            }
        return false;
    }

    /// <summary>Builds the project(s) containing the touched files; groups CS errors by file and tags each file
    /// as one the architect edited (fix it) or pre-existing (leave it, mention in the summary).</summary>
    private async Task<string> BuildTouched(HashSet<string> touched, string root, CancellationToken ct)
    {
        if (touched.Count == 0) return "[System: no files have been changed yet — spawn a coder first.]";
        List<string> projects = touched.Select(f => NearestProject(f, root)).OfType<string>()
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count == 0) return "[System: the changed files aren't in a buildable .NET project — skip the build and write your summary.]";

        Dictionary<string, List<string>> errors = new(StringComparer.OrdinalIgnoreCase);
        bool anyRan = false;
        foreach (string proj in projects)
        {
            (bool ran, string output) = await DotnetBuild(proj, ct);
            if (!ran) continue;
            anyRan = true;
            foreach (KeyValuePair<string, List<string>> kv in CollectCsErrors(output, root))
            {
                if (!errors.TryGetValue(kv.Key, out List<string>? l)) errors[kv.Key] = l = new();
                foreach (string e in kv.Value) if (!l.Contains(e)) l.Add(e);
            }
        }
        if (!anyRan)           return "[System: the project could not be built on this machine — skip the build and write your summary.]";
        if (errors.Count == 0) return "Build succeeded — no compile errors.";

        StringBuilder sb = new();
        sb.AppendLine("Build FAILED. Compile errors grouped by file:");
        bool anyYours = false;
        foreach ((string file, List<string> errs) in errors)
        {
            string? absF = SafeAbs(root, file);
            bool yours   = absF is not null && touched.Contains(absF);
            if (yours) anyYours = true;
            sb.AppendLine($"`{file}` {(yours ? "[you edited this file — fix it]" : "[pre-existing — you did NOT touch this file]")}:");
            foreach (string e in errs) sb.AppendLine($"  - {e}");
        }
        sb.Append(anyYours
            ? "\nFix the errors in the file(s) YOU edited by spawning coders, then call build_project again."
            : "\nEvery error is in a file you did NOT touch — these are pre-existing (common in a large refactor). Do NOT try to fix them; go straight to your summary and state that these pre-existing build errors remain.");
        return sb.ToString();
    }

    /// <summary>Remote build: there's no dotnet on this server for the client's project, so run `dotnet build` on
    /// the CLIENT via its forwarded run_command and hand the raw output back to the architect. (The yours-vs-
    /// pre-existing tagging that BuildTouched does needs local disk; on remote we return the client's output as-is.)</summary>
    private static async Task<string> BuildRemote(Thread parent, HashSet<string> touched, CancellationToken ct)
    {
        if (touched.Count == 0) return "[System: no files have been changed yet — spawn a coder first.]";
        if (!parent.tools.TryGetValue("run_command", out var rc))
            return "[System: no run_command tool is available to build on the client — skip the build and write your summary.]";
        string output = await rc.Execute(JsonSerializer.Serialize(new { command = "dotnet build" }));
        return "Build output from the client (`dotnet build`):\n\n" + output;
    }

    // Appraise how much thinking the request needs → wall-clock budget (seconds) + an awareness line the model is
    // told so it self-paces. Null appraiser ⇒ (null, "") = today's behaviour. Runs once at the start of the request.
    private async Task<(int? grade, int? thinkSeconds, string awareness)> AppraiseThinking(string prompt, string threadKey, CancellationToken ct)
    {
        if (Appraisal is null) return (null, null, "");
        int grade = await Appraisal.Appraise(prompt, ct);
        int secs  = Appraisal.GradeToSeconds(grade);
        string awareness =
              secs < 0   ? " You may think as long as you need."
            : secs <= 10 ? " This needs little or no deliberation — think for at most a moment, then act."
            :              $" You have about {secs} seconds to think — be concise and reach your conclusion within it.";
        Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) appraisal grade {G} → {S}s thinking budget.", threadKey, grade, secs);
        return (grade, secs, awareness);
    }


    // ── Execution: file batching, content seeding, build verify ───────────────────

    private static void RegisterCoderTools(Thread child, string root, FileSnapshots snapshots, CancellationToken ct)
    {
        // Lean executor toolset: locate → preview → read the assigned range, edit, recover. search_files/find_files
        // are included so the Coder can resolve a symbol referenced by its task but living in another file (without
        // them it guesses paths and loop-breaks); list_directory is NOT (with thinking off it invites aimless
        // browsing). preview_file satisfies the preview-before-read gate and keeps context lean on its assigned file.
        ServerFileSystem fs = new(root, ct, snapshots);
        new PreviewFile(fs).Register(child);
        new ReadFile(fs).Register(child);
        new SearchFiles(fs).Register(child);
        new FindFiles(fs).Register(child);
        new EditFile(fs).Register(child);
        new WriteFile(fs).Register(child);
        new RevertFile(root, ct, snapshots).Register(child);   // RevertFile is snapshot-tied — not via FileSystem yet
        new DeleteFile(fs).Register(child);
        new MoveFile(fs).Register(child);
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
