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
/// <summary>Per-CodePhase prompt + sampling override for the coding pipeline (deserialised from the
/// CodeArchitect entry in Agents.json). A null sampling member falls back to the flat agent default,
/// then the server baseline.</summary>
public sealed class PhaseConfig
{
    [JsonPropertyName("systemPrompt")]     public string  SystemPrompt     { get; init; } = "";
    [JsonPropertyName("temperature")]      public double? Temperature      { get; init; }
    [JsonPropertyName("topP")]             public double? TopP             { get; init; }
    [JsonPropertyName("topK")]             public int?    TopK             { get; init; }
    [JsonPropertyName("minP")]             public double? MinP             { get; init; }
    [JsonPropertyName("repeatPenalty")]    public double? RepeatPenalty    { get; init; }
    [JsonPropertyName("presencePenalty")]  public double? PresencePenalty  { get; init; }
    [JsonPropertyName("frequencyPenalty")] public double? FrequencyPenalty { get; init; }
}

internal sealed class CodeArchitect : Agent
{
    public CodeArchitect() { }

    // Coding prompts are verbose and already logged by the pipeline; don't double-log them.
    [JsonIgnore] internal override bool SuppressPromptLog => true;

    // #112: during long exploration the architect goes minutes emitting only tool calls. Every ~90s of
    // tool-only work, force a one-sentence check-in — better UX and it re-anchors purpose against the
    // "search because my history is all searches" momentum that drives over-exploration.
    [JsonIgnore] internal override int NarrationIntervalSeconds => 90;

    // ── State machine: per-CodePhase prompt + sampling ───────────────────────────────────────────────
    // SystemPrompt (base field) holds the INVARIANT [Role] text; each phase supplies its own [Mode] prompt
    // and sampling. Planning = warm/exploratory, Development = cold/precise. Configured in Agents.json.
    [JsonPropertyName("phases")] public Dictionary<string, PhaseConfig>? Phases { get; init; }

    private PhaseConfig? PhaseFor(Thread t)
        => Phases is not null && Phases.TryGetValue(t.Phase.ToString(), out PhaseConfig? p) ? p : null;

    internal override string SystemPromptFor(Thread thread)
    {
        PhaseConfig? phase = PhaseFor(thread);
        if (phase is null || phase.SystemPrompt.Length == 0) return SystemPrompt;   // no phase config → flat
        return $"[Role]\n{SystemPrompt}\n\n[Mode: {thread.Phase}]\n{phase.SystemPrompt}";
    }

    internal override (double? Temperature, double? TopP, int? TopK, double? MinP,
                       double? RepeatPenalty, double? PresencePenalty, double? FrequencyPenalty)
        SamplingFor(Thread thread)
    {
        PhaseConfig? p = PhaseFor(thread);
        return p is null
            ? (null, null, null, null, null, null, null)
            : (p.Temperature, p.TopP, p.TopK, p.MinP, p.RepeatPenalty, p.PresencePenalty, p.FrequencyPenalty);
    }


    // Phase enforcement. Runs before EVERY tool call on this thread (local ServerFileSystem tools AND the
    // client's forwarded edit/write tools), so "no building in Planning" holds on both paths uniformly.
    protected override string? PreToolGuard(Thread thread, ToolTurnState state, string toolName, string callId, string argsJson)
    {
        if (thread.Phase == CodePhase.Planning
            && toolName is "edit_file" or "write_file" or "delete_file" or "move_file" or "build_project")
            return "[System: you are in planning mode — finish your plan and call plan_proposed. Editing and building " +
                   "are disabled until the user approves the plan.]";
        if (thread.Phase == CodePhase.Development && toolName == "plan_proposed")
            return "[System: the plan is already approved — you are building. Use replan only if the plan is wrong.]";
        if (thread.Phase == CodePhase.Planning && toolName == "replan")
            return "[System: you are already in planning — just revise your plan and call plan_proposed.]";
        return null;
    }

    // Track files the architect edits directly, so build_project knows what to build (replaces spawn_coder's
    // touched-tracking). Runs after every tool on both the local and remote paths.
    protected override string PostToolProcess(Thread thread, ToolTurnState state, string toolName, string argsJson, string result)
    {
        if (toolName is "edit_file" or "write_file"
            && !ToolCallParser.IsError(result) && !result.StartsWith("[System:", StringComparison.Ordinal))
        {
            string? path = ToolCallParser.TryExtractJsonString(argsJson, "path");
            if (!string.IsNullOrWhiteSpace(path)) thread.TouchedFiles.Add(path.Trim());
        }
        return result;
    }


    private sealed record CodePlan(List<string> Decisions, List<CodeStep> Steps);
    private sealed record CodeStep(string File, string Range, string Change);

    // Entry for every user turn on a Code thread (from CodePipeline). Registers the phase tools and runs the
    // architect turn(s): a dev_mode / planning_mode hand-off auto-continues into the new mode within the same
    // call. The plan prose lives in History — nothing is stashed; the phase (Thread.Phase) is the only state.
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
            // The architect edits DIRECTLY (no Coder sub-agent) — its reads stay resident, so there is no blind
            // re-read barrier. (Remote: the client's edit_file/write_file are already on the thread.)
            new EditFile(fs).Register(parent);
            new WriteFile(fs).Register(parent);
        }

        // Files edited this turn — the build-error owner tag + "did anything change". Populated by the edit
        // tools via PostToolProcess (works for both the local wrappers and the client's forwarded edit tools).
        parent.TouchedFiles.Clear();

        // Deterministic edit freeze: when the user forbids changes this turn ("planning only") — enforced at the
        // tool layer by PreToolGuard alongside the Planning-mode edit block.
        bool editsForbidden = UserForbadeEdits(prompt);

        // build_project() — build the touched project(s); errors grouped by file, tagged yours vs pre-existing.
        // Remote: the build runs on the client via its forwarded run_command; local: dotnet build on this disk.
        parent.RegisterTool("build_project", BuildProjectSchema,
            async _ => remote ? await BuildRemote(parent, parent.TouchedFiles, cts.Token) : await BuildTouched(parent.TouchedFiles, root, cts.Token),
            displayFormatter: _ => "<!--ari-tool-start:build_project:project-->");

        bool bypass = Environment.GetEnvironmentVariable("ARI_GATE_BYPASS") == "1";

        // ── State transitions (system-driven, not LLM-driven) ──────────────────────────────────────────
        // plan_proposed(payload): the architect calls this the moment its plan is written — while its reads
        // are STILL resident, so the payload is complete. It marks a plan-on-the-table and force-ends the turn.
        // The user then approves (→ CodePipeline moves to Development next turn WITH the payload) or gives
        // feedback (→ stays Planning to revise). In an automated run there's no user, so it self-approves.
        parent.RegisterTool("plan_proposed", PlanProposedSchema, argsJson =>
        {
            parent.HandoffPayload = ToolCallParser.TryExtractJsonString(argsJson, "payload");
            if (bypass) { parent.Phase = CodePhase.Development; return Task.FromResult("[System: plan captured — automated run, building now.]"); }
            parent.PlanProposed = true;
            parent.EndTurnNow   = true;   // clean boundary — nothing else runs this turn
            return Task.FromResult("[System: plan proposed and captured. STOP now — the user will approve it (then you build) or ask for changes (then you revise). Do not build yet.]");
        });
        // replan(reason): from Development, hand back to Planning when the plan turns out wrong/blocked.
        parent.RegisterTool("replan", ReplanSchema, argsJson =>
        {
            parent.Phase        = CodePhase.Planning;
            parent.PlanProposed = false;
            parent.EndTurnNow   = true;
            string reason = ToolCallParser.TryExtractJsonString(argsJson, "reason") ?? "";
            return Task.FromResult($"[System: the plan needs revising — back in planning. Tell the user what you found: {reason}]");
        });

        // Per-turn nudge. The [Mode] system prompt carries the behaviour; this is a short reminder of THIS turn.
        string nudge = parent.Phase == CodePhase.Planning
            ? (bypass
                ? "PLANNING (automated run). Explore leanly, then call plan_proposed with a complete payload — it auto-approves and builds."
                : parent.RevisingPlan
                    // Amend turn: the [Task] above is the user's requested change to a plan you already proposed.
                    // The model's failure mode is writing the revised plan as a PROSE section and never calling
                    // plan_proposed — so forbid prose outright and demand the tool call be the ONLY output.
                    ? "PLANNING — REVISION. The user did not approve your last plan; [Task] is the change they want. Proceed EXACTLY:\n1. Reuse what you already know (read a file ONLY if the change needs a detail you genuinely lack).\n2. Do NOT write the plan, or any part of it, as prose in your message.\n3. Emit ONE plan_proposed tool call whose payload is the FULL revised plan — and output NOTHING ELSE this turn. No lead-in sentence, no prose, no explanation: the tool call is your entire reply. Do NOT build."
                    : "PLANNING. If the request is genuinely vague, ask ONE clarifying question and stop. Otherwise: explore with read tools until you can plan — then PROPOSE, and a proposal is ONE plan_proposed call and NOTHING ELSE (no prose, no lead-in sentence): the full plan is its payload. NEVER write the plan, or any step of it, as prose in your message — prose is not a proposal and leaves the user nothing to approve. Either you're still exploring (call read tools) or you're proposing (call plan_proposed) — never describe the plan in text. Don't over-read.")
            : "DEVELOPMENT. Build the plan from the [Handoff] payload above — edit one file at a time, then build to verify. If the plan is genuinely wrong, call replan.";
        if (editsForbidden)
            nudge += " [The user has forbidden edits this turn — do not build; plan only.]";

        // Development turns carry the architect's handoff payload (the working set — reads don't persist).
        string handoff = parent.Phase == CodePhase.Development && !string.IsNullOrWhiteSpace(parent.HandoffPayload)
            ? $"[Handoff — a plan summary, not the code. Build it now: create the NEW files with write_file; for each EXISTING file, preview_file it once then edit. Never preview a NEW file — it does not exist yet.]\n{parent.HandoffPayload}\n\n"
            : "";
        string response = await SendPrompt(parent, prompt, username,
            augmentedPrompt: $"{handoff}[Task]\n{prompt}\n\n[System]\n{nudge}",
            ct: cts.Token, userMessagePreadded: true, onDelta: onDelta);

        // Deterministic safety net for the amend path (NOT a content heuristic). A revision turn is ALWAYS a
        // proposal: the user already stated the change, so the model never needs to ask a question here — its
        // reply can only be the revised plan. Strong steering (the strict revision nudge) makes it call
        // plan_proposed ~88% of the time; for the rest it writes the plan as prose. Since we KNOW this turn is a
        // proposal (because RevisingPlan is set — no guessing about what the text is), promote that prose to a
        // proposal so the user always gets the chip + Accept/Amend buttons.
        if (parent.RevisingPlan && !parent.PlanProposed)
        {
            Shared.Logger.LogInformation("[CodeArchitect] ({Thread}) revision turn ended without plan_proposed — promoting reply to a proposal.", parent.Key);
            parent.HandoffPayload = response;
            parent.PlanProposed   = true;
            if (!response.Contains("<!--ari-plan-proposed-->", StringComparison.Ordinal))
                response += "\n\n<!--ari-plan-proposed-->";
        }
        return response;
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

    private static readonly object PlanProposedSchema = new
    {
        type = "function",
        function = new
        {
            name = "plan_proposed",
            description = "Call this the moment your plan is written and presented, to put it before the user for approval. " +
                          "This ENDS your turn — do not build. CRITICAL: your file reads/previews do NOT survive into the " +
                          "build stage, so the 'payload' you pass here becomes the build's ENTIRE working context. Call it " +
                          "now, while you still have everything in front of you — make the payload self-contained.",
            parameters = new
            {
                type = "object",
                properties = new { payload = new { type = "string", description =
                    "The complete handoff for the build stage. Include: the ordered plan (files to create/edit and the " +
                    "change to each), AND the exact contracts the build will rely on — the fields/properties/method " +
                    "signatures of the data types you bind to, the pattern of the exemplar you're imitating, and the exact " +
                    "method/lines you're editing. The build will NOT have your reads, so anything it needs must be here." } },
                required = new[] { "payload" }
            }
        }
    };

    private static readonly object ReplanSchema = new
    {
        type = "function",
        function = new
        {
            name = "replan",
            description = "Call this from the build stage to go back to planning — when the approved plan turns out wrong " +
                          "or blocked. Pass what you found so the plan can be revised.",
            parameters = new
            {
                type = "object",
                properties = new { reason = new { type = "string", description = "Why the plan needs revising." } }
            }
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
        if (touched.Count == 0) return "[System: no files have been changed yet — make your edits first.]";
        // TouchedFiles holds project-relative paths (from the edit tool args); resolve to absolute for the
        // NearestProject lookup and the yours-vs-pre-existing tagging below.
        HashSet<string> touchedAbs = touched.Select(f => SafeAbs(root, f) ?? f).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> projects = touchedAbs.Select(f => NearestProject(f, root)).OfType<string>()
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
            bool yours   = absF is not null && touchedAbs.Contains(absF);
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
        if (touched.Count == 0) return "[System: no files have been changed yet — make your edits first.]";
        if (!parent.tools.TryGetValue("run_command", out var rc))
            return "[System: no run_command tool is available to build on the client — skip the build and write your summary.]";
        string output = await rc.Execute(JsonSerializer.Serialize(new { command = "dotnet build" }));
        return "Build output from the client (`dotnet build`):\n\n" + output;
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
