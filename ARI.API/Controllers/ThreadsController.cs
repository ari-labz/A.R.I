using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ARI.API;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ARI.API.Controllers;


[Route("api/v1/threads")]
[ApiController]
public class ThreadsController(ProjectStore projectStore, ARI.API.Auth.UserStore users) : ControllerBase, Microsoft.AspNetCore.Mvc.Filters.IActionFilter
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    // The web client reads SSE events as camelCase (data.type / data.threadKey / data.text). Without
    // this, bare Serialize emits PascalCase ("Type") and the client's event switch silently never matches.
    private static readonly JsonSerializerOptions SseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Maps threadKey → projectId. Lives on ProjectStore (not here) so both this controller and
    // ProjectServiceAdapter — the REST path and the tool-call path — share the exact same state.
    private ConcurrentDictionary<string, string> ThreadProjects => projectStore.ThreadProjects;

    // Pending per-message attachments (ephemeral — cleared after send).
    private static readonly ConcurrentDictionary<string, List<Attachment>> pendingMessageAttachments = new();
    // Filenames of text files written to disk since the last send — injected as a note into platformContext.
    private static readonly ConcurrentDictionary<string, List<string>>     pendingFileNotes          = new();

    // System-context blocks injected by the client (e.g. filesystem skeleton). Merged into the
    // system prompt at send time — never exposed as user-visible thread attachments.
    private static readonly ConcurrentDictionary<string, string> pendingSystemContext = new();

    /// <summary>The name this caller goes by in conversations.</summary>
    private string GetUsername()
    {
        // The name comes from the account as it is now, not from the token, which can be weeks old.
        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int callerId) && users.GetById(callerId) is { } account)
            return string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName;
        string? displayName = User.FindFirstValue("displayName");
        if (!string.IsNullOrEmpty(displayName)) return displayName;
        // Username claim as last resort.
        string? username = User.FindFirstValue(ClaimTypes.Name);
        return string.IsNullOrEmpty(username) ? "User" : username;
    }

    // ── Thread navigation helpers ───────────────────────────────────────────────

    /// <summary>Every action with a {id} in its route passes through here first, so no endpoint can forget the
    /// ownership check: a guest can only touch their own threads. A key with no thread yet is let through — creating
    /// it (stream) stamps the caller as its owner.</summary>
    void Microsoft.AspNetCore.Mvc.Filters.IActionFilter.OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        if (context.RouteData.Values["id"] is not string key || Llm is null) return;
        if (context.ActionDescriptor.RouteValues["action"] is nameof(GetDebugSession)) return;   // its id is a recording, not a thread
        if (Llm.Threads.TryGetValue(key, out ARI.LLM.Thread? thread))
        {
            if (!CanAccessThread(thread)) context.Result = new StatusCodeResult(403);
        }
        // An agent's own sub-thread ("{chat}:{code}") isn't in the table; whoever may see its chat may see it.
        else if (key.LastIndexOf(':') is > 0 and int cut && Llm.Threads.TryGetValue(key[..cut], out ARI.LLM.Thread? owner))
        {
            if (!CanAccessThread(owner)) context.Result = new StatusCodeResult(403);
        }
        // A saved (expired) thread is not in memory, but it still belongs to its owner.
        else if (Llm.GetPersistedInfo(key) is { } saved)
        {
            if (!CanAccessOwner(saved.OwnerId)) context.Result = new StatusCodeResult(403);
        }
        // A key nobody has used yet: only sending the first message (which creates the thread and makes the caller its
        // owner) may use it. Anything else would be reaching into folders left behind by a thread that is gone.
        else if (context.ActionDescriptor.RouteValues["action"] is not (nameof(Stream) or nameof(SetSystemContext) or nameof(NotifyTyping)))
            context.Result = new NotFoundObjectResult(new { error = "No such thread." });
        else if (!User.IsInRole(ARI.API.Auth.Roles.Admin) && !ARI.API.Auth.ThreadAccess.IsClientKey(key))
            context.Result = new StatusCodeResult(403);
    }

    private bool CanAccessOwner(string? ownerId) =>
        User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin
        || (ownerId is not null && ownerId == User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier));

    void Microsoft.AspNetCore.Mvc.Filters.IActionFilter.OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context) { }

    /// <summary>True if the current caller may read/write this thread.</summary>
    private bool CanAccessThread(ARI.LLM.Thread thread)
    {
        if (User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin) return true;
        string? callerId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        return thread.OwnerId == callerId;
    }

    /// <summary>Finds an existing live thread by key (user-facing or internal).</summary>
    private ARI.LLM.Thread? FindThread([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return null;
        Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? t);
        return t;
    }

    /// <summary>Gets or creates the correct thread for the given key, routing to Code or Dialogue.</summary>
    private ARI.LLM.Thread GetOrCreateThread([FromRoute(Name = "id")] string threadKey)
    {
        bool isCode = Llm!.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? existing)
                      && existing.Pipeline == ARI.LLM.ThreadPipeline.Code;
        ARI.LLM.Thread thread = isCode ? Llm.GetOrCreateCodeThread(threadKey) : Llm.GetOrCreateDialogueThread(threadKey);
        bool isAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        // Track who created the thread so guests only see their own.
        string? callerId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        thread.OwnerId ??= callerId;
        // Memory and Engram belong to the Master's and admins' own chats. An admin replying inside someone else's thread
        // must not turn it into one of theirs.
        thread.HasMemoryAccess = isAdmin && thread.OwnerId == callerId;
        return thread;
    }

    /// <summary>True if the caller owns the project, or is an admin (who can use any project).</summary>
    private bool CanUseProject(Project project)
        => User.FindFirstValue(ClaimTypes.Role) == ARI.API.Auth.Roles.Admin
           || (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int callerId) && project.OwnerId == callerId);

    // ── Thread endpoints ────────────────────────────────────────────────────────

    /// <summary>Your threads, newest first (an admin's list holds everyone's). Take a slice with limit and offset.</summary>
    [HttpGet]
    public IActionResult GetThreads() => BuildThreadList(includeInternal: false);

    /// <summary>Every thread, including the internal ones (memory work, dreams, other people's conversations): the admin's debugging view.</summary>
    [HttpGet("~/api/v1/admin/debug/threads")]
    public IActionResult GetAllThreads() => BuildThreadList(includeInternal: true);

    private IActionResult BuildThreadList(bool includeInternal)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        IReadOnlyDictionary<string, ARI.LLM.Thread> allThreads = Llm.Threads;

        bool   callerIsAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        string? selfId       = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        string? callerId     = callerIsAdmin ? null : selfId;

        // An admin sees everyone's threads, so each one that belongs to somebody else carries that person's
        // name and the sidebar groups it under them — otherwise it reads as if the admin's list were shared.
        Dictionary<string, string?> ownerNames = new();
        string? OwnerNameFor(string? ownerId)
        {
            if (!callerIsAdmin || ownerId == selfId) return null;
            if (ownerId is null) return "No owner";   // public Discord chats, integrations
            if (!ownerNames.TryGetValue(ownerId, out string? name))
            {
                ARI.API.Auth.User? u = int.TryParse(ownerId, out int id) ? users.GetById(id) : null;
                ownerNames[ownerId] = name = u is null ? null : (string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName);
            }
            return name;
        }

        List<ThreadEntry> threads = allThreads
            .Where(kvp => kvp.Value.Pipeline is ARI.LLM.ThreadPipeline.Dialogue or ARI.LLM.ThreadPipeline.Code or ARI.LLM.ThreadPipeline.Speech
                          && !kvp.Value.Internal
                          && (callerIsAdmin || kvp.Value.OwnerId == callerId))
            .Select(kvp =>
            {
                string? projectId   = ThreadProjects.TryGetValue(kvp.Key, out string? pid) ? pid : null;
                string? projectName = projectId is not null ? projectStore.Get(projectId)?.Name : null;
                return new ThreadEntry(kvp.Key, AgentName: null, IsInternal: false,
                    LastMessageAt: kvp.Value.LastMessageAt,
                    MessageCount: kvp.Value.History.Count(m => m is Prompt or ARI.LLM.Response),
                    State: kvp.Value.State.ToString().ToLowerInvariant(),
                    IsCodeMode: kvp.Value.Pipeline == ARI.LLM.ThreadPipeline.Code,
                    ProjectName: projectName, ProjectId: projectId,
                    Pipeline: kvp.Value.Pipeline.ToString().ToLowerInvariant(),
                    Title: kvp.Value.Title, OwnerName: OwnerNameFor(kvp.Value.OwnerId));
            })
            .ToList();

        // Persistent Threads: conversations that expired but were kept. Same visibility rule as live ones — a guest's
        // request never even reads another user's entry into the response.
        foreach (ARI.LLM.LLMModule.PersistedThreadInfo saved in Llm.ListPersistedThreads())
        {
            if (!callerIsAdmin && saved.OwnerId != callerId) continue;
            threads.Add(new ThreadEntry(saved.Key, AgentName: null, IsInternal: false, LastMessageAt: saved.LastMessageAt, MessageCount: 0,
                State: "expired", IsCodeMode: false, Pipeline: "dialogue", Title: saved.Title, OwnerName: OwnerNameFor(saved.OwnerId)));
        }

        if (includeInternal)
        {
            HashSet<string> userKeys = new(threads.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            IEnumerable<ThreadEntry> internalThreads = allThreads
                .Where(kvp => !userKeys.Contains(kvp.Key))
                .Select(kvp => new ThreadEntry(kvp.Key, kvp.Value.Pipeline.ToString(), IsInternal: true,
                    kvp.Value.LastMessageAt, kvp.Value.History.Count));
            threads.AddRange(internalThreads);
        }

        // An agent's own sub-thread (the Memory walker's, say) is listed under the chat it belongs to, so the debug view can
        // show it indented beneath its parent and open it on its own.
        if (includeInternal)
        {
            List<ThreadEntry> owned = new();
            void AddOwned(ARI.LLM.Thread parent)
            {
                foreach (ARI.LLM.Thread child in parent.Children.Where(c => c.Code is not null))
                {
                    owned.Add(new ThreadEntry(child.Key, child.OwnerAgent, IsInternal: true, child.LastMessageAt,
                        child.History.Count(m => m is Prompt or ARI.LLM.Response), State: child.State.ToString().ToLowerInvariant(),
                        ParentKey: parent.Key, Label: child.Label));
                    AddOwned(child);
                }
            }
            foreach (ThreadEntry t in threads.ToList())
                if (allThreads.TryGetValue(t.Key, out ARI.LLM.Thread? live)) AddOwned(live);
            threads.AddRange(owned);
        }

        return Ok(this.Page(threads.OrderByDescending(t => t.LastMessageAt).ToList()));
    }

    [HttpPost]
    public IActionResult NewThread([FromBody] NewThreadRequest? req = null)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        // Desktop (Electron) clients get a "client-" key so they're distinguishable from browser ("web-") threads.
        string key = $"{(req?.Desktop == true ? "client" : "web")}-{Guid.NewGuid():N}";
        // A project can only be attached by someone who may use it: its owner, or an admin. Anything else is
        // reported as "not found" so a guess at someone else's project id reveals nothing.
        Project? project = null;
        if (!string.IsNullOrWhiteSpace(req?.ProjectId))
        {
            project = projectStore.Get(req.ProjectId);
            if (project is null || !CanUseProject(project))
                return NotFound(new { error = "Project not found." });
            projectStore.BindThread(key, req!.ProjectId!);
        }
        // Pre-register in LLMModule so the newThread event fires immediately and
        // all sidebar observers see the thread without waiting for the first message.
        // An explicit pipeline selection wins; otherwise a bound project that isn't a note vault opens in code-mode.
        if (Enum.TryParse(req?.Pipeline, ignoreCase: true, out ARI.LLM.ThreadPipeline selected) && !int.TryParse(req?.Pipeline, out _)
            && selected is ARI.LLM.ThreadPipeline.Dialogue or ARI.LLM.ThreadPipeline.Code or ARI.LLM.ThreadPipeline.Speech)
            Llm.ForcePipeline(key, selected);
        else if (req?.Pipeline is { Length: > 0 } graphId && Llm.ReplyPipelines().Any(p => p.Id.Equals(graphId, StringComparison.OrdinalIgnoreCase)))
        {
            Llm.GetOrCreateDialogueThread(key);
            Llm.SetThreadPipeline(key, graphId);
        }
        else if (project is not null && ProjectCodeRules.RunsAsCode(project))
            Llm.ForceCodeThread(key);
        else
            Llm.GetOrCreateDialogueThread(key);
        // Stamp the creator now, not on the first message — otherwise a brand-new thread belongs to nobody, so a
        // guest's own empty thread isn't in their list and an admin can't tell whose it is.
        if (Llm.Threads.TryGetValue(key, out ARI.LLM.Thread? created))
        {
            created.HasMemoryAccess = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
            created.OwnerId ??= User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        }
        return Ok(new { key });
    }

    /// <summary>
    /// The pipelines a thread can run on, as <c>{ id, label }</c>: the built-in "dialogue" (Default), "code" and "speech", then
    /// every pipeline made in the control panel that can run. The client builds its selector from this list, so a new pipeline
    /// appears there as soon as it is saved.
    /// </summary>
    [HttpGet("~/api/v1/pipelines")]
    public IActionResult GetPipelines()
    {
        List<object> list = [new { id = "dialogue", label = "Default" }, new { id = "code", label = "Code" }, new { id = "speech", label = "Talk" }];
        foreach ((string id, string name) in Llm?.ReplyPipelines() ?? [])
            if (!id.Equals("default", StringComparison.OrdinalIgnoreCase)) list.Add(new { id, label = name });
        return Ok(list);
    }

    public sealed record SetPipelineRequest(string? Pipeline);

    /// <summary>Switches a thread to another pipeline for its next reply: "dialogue" is Default, "code" and "speech" are the
    /// built-in modes, anything else is a pipeline made in the control panel. The thread keeps its history, but the next
    /// reply has to read it all again (the new pipeline's agents have not seen it).</summary>
    [HttpPut("{id}/pipeline")]
    public IActionResult SetPipeline([FromRoute(Name = "id")] string threadKey, [FromBody] SetPipelineRequest req)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread)) return NotFound();
        if (!CanAccessThread(thread)) return StatusCode(403);
        string? id = req?.Pipeline;
        if (string.IsNullOrWhiteSpace(id)) return BadRequest(new { error = "pipeline is required." });
        if (id.Equals("code", StringComparison.OrdinalIgnoreCase)) Llm.ForcePipeline(threadKey, ARI.LLM.ThreadPipeline.Code);
        else if (id.Equals("speech", StringComparison.OrdinalIgnoreCase)) Llm.ForcePipeline(threadKey, ARI.LLM.ThreadPipeline.Speech);
        else
        {
            if (id.Equals("dialogue", StringComparison.OrdinalIgnoreCase)) id = "default";
            Llm.ForcePipeline(threadKey, ARI.LLM.ThreadPipeline.Dialogue);
            if (!Llm.SetThreadPipeline(threadKey, id)) return NotFound(new { error = $"No pipeline '{id}' can run." });
        }
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Returns thread metadata plus full history. The primary polling endpoint for streaming threads.
    /// Poll at ~150ms while thread.state == "streaming"; stop once it is anything else (active/inactive/…).
    /// DebugRequestJson / DebugResponseText are excluded here — use GET /admin/debug/threads/{id} for those.
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetThread([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        bool expired = false;
        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
        {
            // Not in memory: it may be an expired thread kept by Persistent Threads, rebuilt here for viewing.
            thread = Llm.LoadPersistedForView(threadKey);
            if (thread is null) return NotFound();
            expired = true;
        }

        if (!CanAccessThread(thread)) return StatusCode(403);

        return Ok(new
        {
            key           = threadKey,
            state         = expired ? "expired" : thread.State.ToString().ToLowerInvariant(),
            pipeline      = thread.GraphId ?? thread.Pipeline.ToString().ToLowerInvariant(),
            isInternal    = thread.Internal,
            isProcessing  = !expired && Llm.IsThreadProcessing(threadKey),
            lastMessageAt = thread.LastMessageAt,
        });
    }

    /// <summary>
    /// Returns thread history with DebugRequestJson, DebugResponseText and Reasoning exposed on Response
    /// items, plus any spawned sub-threads (a CodeArchitect's plan + per-task Coder threads) nested under
    /// <c>children</c> so the otherwise-invisible orchestration is fully inspectable. Shape:
    /// <c>{ key, label, isInternal, pipeline, history: [...], children: [ {same shape}, ... ] }</c>.
    /// Used exclusively by the control-panel Debug Threads pane — not for normal clients.
    /// </summary>
    [HttpGet("~/api/v1/admin/debug/threads/{id}")]
    public IActionResult GetThreadDebug([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        if (!Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? thread))
        {
            // An agent's own sub-thread isn't a thread of its own in the table: it hangs off its parent as "{parent}:{code}".
            thread = null;
            ARI.LLM.Thread? Find(ARI.LLM.Thread t) => t.Children.Select(c => string.Equals(c.Key, threadKey, StringComparison.OrdinalIgnoreCase) ? c : Find(c)).FirstOrDefault(c => c is not null);
            foreach (ARI.LLM.Thread root in Llm.Threads.Values) if ((thread = Find(root)) is not null) break;
            if (thread is null) return NotFound();
        }

        return Ok(SerializeDebugThread(thread));
    }

    // Test-only: injects a fixed transcript directly into a thread's history, no LLM call, so Engram can be exercised repeatedly against the same input.
    [HttpPost("~/api/v1/admin/debug/threads/{id}/seed")]
    public IActionResult SeedDebugHistory([FromRoute(Name = "id")] string threadKey, [FromBody] List<SeedTurn> turns)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        ARI.LLM.Thread thread = Llm.GetOrCreateDialogueThread(threadKey);
        DateTime t = DateTime.Now.AddMinutes(-turns.Count);
        foreach (SeedTurn turn in turns)
        {
            t = t.AddMinutes(1);
            ThreadItem item = turn.Speaker.Equals("ari", StringComparison.OrdinalIgnoreCase)
                ? new Response { State = State.Complete, Content = ContentBlock.Parse(turn.Text), Timestamp = t }
                : new Prompt { Text = turn.Text, AuthorName = turn.AuthorName ?? "User", Timestamp = t };
            thread.History.Add(item);
        }
        return Ok(new { threadKey, added = turns.Count });
    }

    public record SeedTurn(string Speaker, string Text, string? AuthorName = null);

    // Test-only: runs Engram's Classify+Extract stages and returns the raw entity list — never touches Save.
    [HttpPost("~/api/v1/admin/debug/threads/{id}/extractOnly")]
    public async Task<IActionResult> DebugExtractOnly([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        List<(string Entity, bool IsNew, string Excerpt, bool Sensitive)> entities = await Llm.DebugExtractOnly(threadKey);
        return Ok(entities.Select(e => new { e.Entity, e.IsNew, e.Excerpt, e.Sensitive }));
    }

    /// <summary>Lists today's recorded session files from disk. Includes completed Engram sweeps and other
    /// ephemeral threads that are no longer in the live registry. (raw session data).</summary>
    [HttpGet("~/api/v1/admin/debug/sessions")]
    public IActionResult GetDebugSessions()
    {
        List<ARI.LLM.SessionRecorder.DiskSession> sessions = ARI.LLM.SessionRecorder.ListTodaysSessions();
        return Ok(this.Page(sessions.Select(s => new { stem = s.Stem, bytes = s.Bytes }).ToList()));
    }

    /// <summary>Returns the raw events from a disk-recorded session file.</summary>
    [HttpGet("~/api/v1/admin/debug/sessions/{id}")]
    public IActionResult GetDebugSession([FromRoute(Name = "id")] string stem)
    {
        List<Dictionary<string, object?>>? lines = ARI.LLM.SessionRecorder.ReadSessionFile(stem);
        if (lines is null) return NotFound();
        return Ok(lines);
    }

    /// <summary>Recursively serialises a thread for the Debug pane: its history (with reasoning + raw
    /// request/response) and every sub-thread it spawned. Debug-only.</summary>
    private static object SerializeDebugThread(ARI.LLM.Thread thread) => new
    {
        key        = thread.Key,
        label      = thread.Label,
        isInternal = thread.Internal,
        pipeline   = thread.GraphId ?? thread.Pipeline.ToString().ToLowerInvariant(),
        state      = thread.State.ToString().ToLowerInvariant(),
        history    = thread.History.Select(DebugItem).ToList(),
        code       = thread.Code,
        children   = thread.Children.Select(SerializeDebugThread).ToList(),
    };

    private static object DebugItem(ThreadItem item)
        => item is Response r
            ? new
            {
                type                      = "ariResponse",
                timestamp                 = r.Timestamp,
                state                     = r.State.ToString().ToLowerInvariant(),
                content                   = r.ContentText,
                isStreaming               = r.IsStreamingJson,
                thinkingSeconds           = r.ThinkingSeconds,
                appraisalGrade            = r.AppraisalGrade,
                appraisalSeconds          = r.AppraisalSeconds,
                recallNotes               = r.RecallNotes,
                contextSummary            = r.ContextSummary,
                completionTokens          = r.Data.CompletionTokens,
                outputTokenLimit          = r.Data.OutputTokenLimit,
                promptTokens              = r.Data.PromptTokens,
                contextTokenLimit         = r.Data.ContextTokenLimit,
                estimatedTextPromptTokens = r.Data.EstimatedTextPromptTokens,
                hadImageAttachments       = r.Data.HadImageAttachments,
                imageTokenLimit           = r.Data.ImageTokenLimit,
                debugRequestJson          = r.Data.DebugRequestJson,
                debugResponseText         = r.Data.DebugResponseText,
                reasoning                 = r.Reasoning,
                trace                     = r.Trace,
            }
            : (object)item;

    /// <summary>A thread's messages, oldest first. Cancelled responses are hidden unless <c>raw</c> is set; streaming
    /// responses are included so watching clients can render the in-progress reply. A thread that has expired
    /// (kept by Persistent Threads) is rebuilt just to be read.</summary>
    [HttpGet("{id}/messages")]
    public IActionResult GetMessages([FromRoute(Name = "id")] string threadKey, [FromQuery] bool raw = false)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        ARI.LLM.Thread? histThread = FindThread(threadKey) ?? Llm.LoadPersistedForView(threadKey);
        if (histThread is not null && !CanAccessThread(histThread)) return StatusCode(403);
        List<ThreadItem> items = raw
            ? (histThread?.History ?? new())
            : (histThread?.History ?? new())
                .Where(i => i.IsVisible && i is not Response { State: State.Cancelled })
                .ToList();

        return Ok(this.Page(items));
    }

    [HttpGet("{id}/export")]
    public IActionResult ExportLog([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        ARI.LLM.Thread? exportThread = FindThread(threadKey) ?? Llm.LoadPersistedForView(threadKey);
        if (exportThread is not null && !CanAccessThread(exportThread)) return StatusCode(403);
        List<ThreadItem> items = exportThread?.History ?? new();
        string log = string.Join("\n\n", items.Select(i => i.ToString()));
        byte[] bytes = Encoding.UTF8.GetBytes(log);
        return File(bytes, "text/plain", $"ari-{threadKey}-{DateTime.Now:yyyyMMdd-HHmm}.txt");
    }

    /// <summary>
    /// Global SSE event stream — one connection per client, covers all threads.
    /// Event types: newThread | streaming | streamingFinished | threadDeleted | threadUpdated | threadBound
    /// </summary>
    [HttpGet("~/api/v1/events")]
    public async Task Events(CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: {\"error\":\"not ready\"}\n\n", cancellationToken);
            return;
        }

        bool   evtCallerIsAdmin = User.FindFirstValue(System.Security.Claims.ClaimTypes.Role) == ARI.API.Auth.Roles.Admin;
        string? evtCallerId     = evtCallerIsAdmin ? null : User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

        Channel<AppEvent> channel = Channel.CreateUnbounded<AppEvent>(new UnboundedChannelOptions { SingleReader = true });
        using IDisposable sub = Llm.Subscribe(channel);

        while (!cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            AppEvent? evt;
            try { evt = await channel.Reader.ReadAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Response.WriteAsync(": ping\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            // Guests only receive events for threads they own.
            if (!evtCallerIsAdmin)
            {
                Llm.Threads.TryGetValue(evt.ThreadKey, out ARI.LLM.Thread? evtThread);
                string? evtOwner = evtThread?.OwnerId ?? evt.OwnerId;
                if (evtOwner is null || evtOwner != evtCallerId) continue;
            }

            string payload = JsonSerializer.Serialize(evt, SseJson);
            await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Per-thread live status (SSE): what the thread is doing, and any permission request waiting for an answer.
    /// </summary>
    [HttpGet("{id}/watch")]
    public async Task Watch([FromRoute(Name = "id")] string threadKey, CancellationToken cancellationToken)
    {
        Response.Headers[HeaderNames.ContentType]  = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"]      = "no";

        if (Llm is null)
        {
            await Response.WriteAsync("data: {\"error\":\"not ready\"}\n\n", cancellationToken);
            return;
        }

        if (Llm.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? watchThread) && !CanAccessThread(watchThread))
        {
            Response.StatusCode = 403;
            return;
        }

        Channel<bool?> channel = Channel.CreateUnbounded<bool?>(new UnboundedChannelOptions { SingleReader = true });
        using IDisposable watchHandle = Llm.WatchThread(threadKey, channel);

        // Send initial state so the client can sync immediately on connect.
        await SendWatchEvent(threadKey, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait up to 20s for an update; send a keep-alive ping either way.
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            bool? signal;
            try
            {
                signal = await channel.Reader.ReadAsync(timeoutCts.Token);
                while (channel.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Response.WriteAsync(": ping\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                continue;
            }

            if (signal is null)
            {
                await Response.WriteAsync("data: {\"deleted\":true}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                return;
            }

            await SendWatchEvent(threadKey, cancellationToken);
        }
    }

    private async Task SendWatchEvent([FromRoute(Name = "id")] string threadKey, CancellationToken ct)
    {
        string status = (Llm?.IsEngramSweeping(threadKey)  ?? false) ? "remembering"
                      : Llm?.GetThreadPhase(threadKey) switch
                        {
                            ARI.LLM.ThreadPhase.Prefilling  => "prefilling",
                            ARI.LLM.ThreadPhase.Thinking    => "thinking",
                            ARI.LLM.ThreadPhase.Typing      => "typing",
                            ARI.LLM.ThreadPhase.Researching => "researching",
                            ARI.LLM.ThreadPhase.Generating  => "generating",
                            ARI.LLM.ThreadPhase.Waiting     => "waiting",
                            ARI.LLM.ThreadPhase.Working     => "working",
                            _                              => (Llm?.IsThreadProcessing(threadKey) ?? false) ? "prefilling" : "idle",
                        };
        bool isCodeMode = Llm?.Threads.TryGetValue(threadKey, out ARI.LLM.Thread? wt) == true && wt?.Pipeline == ARI.LLM.ThreadPipeline.Code;
        // A tool waiting on the user's OK (a destructive git/gh command) — the app shows its Allow/Deny prompt.
        object? approval = ARI.LLM.ToolApprovals.Get(threadKey) is { } pending
            ? new { id = pending.Id, title = pending.Title, command = pending.Command }
            : null;
        string payload  = JsonSerializer.Serialize(new { status, isCodeMode, approval });
        await Response.WriteAsync($"data: {payload}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>The user's answer to a tool's approval prompt (see ToolApprovals). 404 if it's no longer waiting.</summary>
    [HttpPost("{id}/approvals/{approvalId}")]
    public IActionResult AnswerApproval([FromRoute(Name = "id")] string threadKey, [FromRoute(Name = "approvalId")] string id, [FromBody] ApprovalAnswer body)
    {
        return ARI.LLM.ToolApprovals.Resolve(threadKey, id, body?.Allow == true) ? Ok() : NotFound();
    }

    [HttpPost("~/api/v1/commands")]
    public async Task<IActionResult> RunCommand([FromBody] CommandRequest req)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (string.IsNullOrWhiteSpace(req.Input)) return BadRequest("Input is required.");
        if (req.ThreadKey is not null)
        {
            // The same rule as every thread route: a live thread must be yours, a saved one too, and a key nobody has used
            // can only be one the server hands out.
            if (Llm.Threads.TryGetValue(req.ThreadKey, out ARI.LLM.Thread? target)) { if (!CanAccessThread(target)) return StatusCode(403); }
            else if (Llm.GetPersistedInfo(req.ThreadKey) is { } savedThread) { if (!CanAccessOwner(savedThread.OwnerId)) return StatusCode(403); }
            else if (!User.IsInRole(ARI.API.Auth.Roles.Admin) && !ARI.API.Auth.ThreadAccess.IsClientKey(req.ThreadKey)) return StatusCode(403);
        }

        bool isMaster = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) == ARI.Common.Shared.MasterUserId;
        string? result = await Llm.HandleCommand(req.ThreadKey, req.Input, isMaster);
        if (result is null) return BadRequest(new { error = $"Unknown command: {req.Input}" });
        return Ok(new { result });
    }

    // ── Attachments ─────────────────────────────────────────────────────────────

    private static readonly HashSet<string> ImageMimes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };

    // Mime types that are binary and cannot be read as plain text.
    private static readonly HashSet<string> BinaryMimes = new(StringComparer.OrdinalIgnoreCase)
        { "application/pdf", "application/zip", "application/x-zip-compressed",
          "application/octet-stream", "application/x-rar-compressed", "application/x-7z-compressed",
          "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
          "application/vnd.openxmlformats-officedocument.presentationml.presentation",
          "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint" };

    [HttpPost("{id}/files")]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> AddAttachment([FromRoute(Name = "id")] string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        // Only the file's own name is ever used: a client can send "../../x" or a full path as the filename.
        string fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || ProjectStore.IsProtectedPath(fileName)) return BadRequest("The file has no usable name.");

        string mime = file.ContentType ?? "application/octet-stream";
        bool isZip  = mime is "application/zip" or "application/x-zip-compressed"
                   || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Resolve the target directory — project dir if already bound, otherwise the thread's scratchpad.
        string targetDir = FindThread(threadKey)?.FilesystemRoot is { } root
                        && !root.StartsWith(Paths.ServerDir("Scratchpad"), StringComparison.OrdinalIgnoreCase)
            ? root
            : Paths.ScratchpadDir(threadKey);

        // ── Zip: extract each text file into the target directory ─────────────────
        if (isZip)
        {
            using MemoryStream zipStream = new();
            await file.CopyToAsync(zipStream);
            zipStream.Position = 0;

            List<string> extracted = new();
            List<string> skipped   = new();

            string targetRoot = Path.GetFullPath(targetDir);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read);
            // A small zip can unpack to something enormous; refuse one that claims too many files or too much data.
            if (archive.Entries.Count > 5000 || archive.Entries.Sum(e => e.Length) > 200L * 1024 * 1024)
                return BadRequest(new { error = "That zip is too big to unpack (5000 files or 200 MB at most)." });
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name.StartsWith('.'))        continue;
                if (IsSkippedZipPath(entry.FullName))  continue;

                // A crafted zip can name an entry "../../x" to escape the folder ("zip slip"): refuse any
                // entry that does not land inside the target.
                string destPath = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destPath.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || ProjectStore.PassesThroughLink(targetRoot, destPath))
                {
                    skipped.Add(entry.FullName);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using FileStream fs = System.IO.File.Create(destPath);
                await entry.Open().CopyToAsync(fs);
                extracted.Add(entry.FullName);
            }

            return Ok(new { zip = fileName, extracted, skipped });
        }

        // ── Normal single file ────────────────────────────────────────────────────
        if (BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(fileName).TrimStart('.').ToUpper()} files cannot be attached — binary formats are not supported." });

        // All files land on the filesystem (scratchpad or project dir); ARI reads them via read_file.
        Directory.CreateDirectory(targetDir);
        string fileDest = Path.Combine(targetDir, fileName);
        if (ProjectStore.PassesThroughLink(targetDir, fileDest)) return BadRequest(new { error = "That file name is not allowed." });
        await using FileStream destFs = System.IO.File.Create(fileDest);
        await file.CopyToAsync(destFs);
        pendingFileNotes.GetOrAdd(threadKey, _ => new()).Add(fileName);

        return Ok(new { name = fileName });
    }

    /// <summary>Extensions treated as plain text and extracted from zips.</summary>
    /// <summary>Zip paths to silently skip (build output, deps, hidden dirs).</summary>
    private static bool IsSkippedZipPath(string fullName) =>
        ProjectStore.IsProtectedPath(fullName) ||
        fullName.Contains("/bin/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/obj/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/node_modules/",StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/.git/",       StringComparison.OrdinalIgnoreCase) ||
        fullName.Contains("/.vs/",        StringComparison.OrdinalIgnoreCase) ||
        fullName.StartsWith("__MACOSX/",  StringComparison.OrdinalIgnoreCase);

    [HttpDelete("{id}/files")]
    public IActionResult RemoveFile([FromRoute(Name = "id")] string threadKey, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path is required.");

        string rootAbs = Path.GetFullPath(FindThread(threadKey)?.FilesystemRoot ?? Paths.ScratchpadDir(threadKey));
        string absPath = Path.GetFullPath(Path.Combine(rootAbs, path));
        // The trailing separator matters: without it "/x/Foo" would also accept "/x/Foo-secret/...".
        if (!absPath.StartsWith(rootAbs.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || ProjectStore.IsProtectedPath(Path.GetRelativePath(rootAbs, absPath))
            || ProjectStore.PassesThroughLink(rootAbs, absPath))
            return BadRequest("Access denied.");

        if (System.IO.File.Exists(absPath)) System.IO.File.Delete(absPath);
        return Ok();
    }

    [HttpGet("{id}/files")]
    public IActionResult GetAttachments([FromRoute(Name = "id")] string threadKey)
    {
        static bool IsImageExt(string ext) => ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

        // All thread files live on disk (scratchpad or project dir).
        string scratchpad = Paths.ScratchpadDir(threadKey);
        string fileRoot   = FindThread(threadKey)?.FilesystemRoot ?? scratchpad;

        IEnumerable<object> diskFiles = Directory.Exists(fileRoot)
            ? Directory.GetFiles(fileRoot, "*", SearchOption.AllDirectories)
                       .Where(p => !ProjectStore.IsProtectedPath(Path.GetRelativePath(fileRoot, p)) && !ProjectStore.PassesThroughLink(fileRoot, p))
                       .Select(p =>
                       {
                           string rel = Path.GetRelativePath(fileRoot, p);
                           string ext = Path.GetExtension(p).ToLowerInvariant();
                           bool isImg = IsImageExt(ext);
                           string url = $"/api/v1/threads/{Uri.EscapeDataString(threadKey)}/files/content?path={Uri.EscapeDataString(rel)}";
                           return (object)new { Name = rel, Url = url, IsImage = isImg };
                       })
            : Enumerable.Empty<object>();

        return Ok(this.Page(diskFiles.ToList()));
    }

    // ── Promote scratchpad to project ──────────────────────────────────────────

    [HttpPost("{id}/promote")]
    public IActionResult PromoteToProject([FromRoute(Name = "id")] string threadKey, [FromBody] PromoteToProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Name))
            return BadRequest(new { error = "Name is required." });

        string scratchpadDir = Paths.ScratchpadDir(threadKey);
        ARI.LLM.Thread? thread = FindThread(threadKey);
        string? currentRoot = thread?.FilesystemRoot;

        // Only scratchpad-rooted threads can be promoted (not already-bound projects).
        if (currentRoot is null || !currentRoot.StartsWith(scratchpadDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(scratchpadDir))
                return BadRequest(new { error = "No scratchpad to promote." });
        }

        if (Modules.Projects is not { } svc)
            return StatusCode(503, new { error = "Project service not available." });

        // Create the project record (ServerFs — destination is on this server).
        ProjectSummary? summary = svc.Create(req.Name.Trim(), req.Category, "ServerFs");
        if (summary is null) return StatusCode(500, new { error = "Failed to create project record." });

        Project? project = projectStore.Get(summary.Id);
        // The project belongs to whoever made it, so a guest keeps access to their own promoted files.
        if (project is not null && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out int promoterId) && !User.IsInRole(ARI.API.Auth.Roles.Admin))
        {
            projectStore.Update(project with { OwnerId = promoterId });
            project = projectStore.Get(summary.Id);
        }
        if (project?.RootPath is not { } destDir)
            return StatusCode(500, new { error = "Project has no server root." });

        // Move files from scratchpad into the new project dir.
        if (Directory.Exists(scratchpadDir))
        {
            // If CreateServerFolder already created the destination, remove it so Move can replace it.
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.Move(scratchpadDir, destDir);
        }

        // Bind the thread to the new project so file tools continue working.
        svc.BindThread(threadKey, summary.Id);

        return Ok(new { projectId = summary.Id, name = summary.Name });
    }

    // ── Message Attachments (ephemeral — cleared after send) ────────────────────

    [HttpPost("{id}/messageAttachments")]
    [RequestSizeLimit(25L * 1024 * 1024)]
    public async Task<IActionResult> AddMessageAttachment([FromRoute(Name = "id")] string threadKey, IFormFile file)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        string fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || ProjectStore.IsProtectedPath(fileName)) return BadRequest("The file has no usable name.");

        string mime    = file.ContentType ?? "application/octet-stream";
        bool   isImage = ImageMimes.Contains(mime);

        if (!isImage && BinaryMimes.Contains(mime))
            return StatusCode(415, new { error = $"{Path.GetExtension(fileName).TrimStart('.').ToUpper()} files are not supported — only images and plain text files can be attached to a message." });

        string content;
        if (isImage)
        {
            using MemoryStream ms = new();
            await file.CopyToAsync(ms);
            content = Convert.ToBase64String(ms.ToArray());
        }
        else
        {
            using System.IO.StreamReader reader = new(file.OpenReadStream());
            content = await reader.ReadToEndAsync();
        }

        // Large text files are promoted to the thread scratchpad instead of being inlined.
        if (!isImage && content.Length > 10_000)
        {
            Directory.CreateDirectory(Paths.ScratchpadDir(threadKey));
            string destPath = Path.Combine(Paths.ScratchpadDir(threadKey), fileName);
            if (ProjectStore.PassesThroughLink(Paths.ScratchpadDir(threadKey), destPath)) return BadRequest(new { error = "That file name is not allowed." });
            await System.IO.File.WriteAllTextAsync(destPath, content);
            return Ok(new { name = fileName, isImage = false, mimeType = mime, promoted = true });
        }

        // Images are also saved to the scratchpad so ARI can pass them to image generation tools.
        if (isImage)
        {
            string scratchDir = Paths.ScratchpadDir(threadKey);
            Directory.CreateDirectory(scratchDir);
            byte[] imgBytes = Convert.FromBase64String(content);
            if (ProjectStore.PassesThroughLink(scratchDir, Path.Combine(scratchDir, fileName))) return BadRequest(new { error = "That file name is not allowed." });
            await System.IO.File.WriteAllBytesAsync(Path.Combine(scratchDir, fileName), imgBytes);
        }

        Attachment attachment = new Attachment { Id = Guid.NewGuid().ToString("N"), Name = fileName, Content = content, IsImage = isImage, MimeType = mime };
        List<Attachment> msgList = pendingMessageAttachments.GetOrAdd(threadKey, _ => new());
        msgList.RemoveAll(a => a.Name == attachment.Name);
        msgList.Add(attachment);
        return Ok(new { id = attachment.Id, name = fileName, isImage, mimeType = mime, content, promoted = false });
    }

    [HttpDelete("{id}/messageAttachments/{attachmentId}")]
    public IActionResult RemoveMessageAttachment([FromRoute(Name = "id")] string threadKey, [FromRoute(Name = "attachmentId")] string id)
    {
        if (pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? list))
            list.RemoveAll(a => a.Id == id);
        return Ok();
    }

    [HttpGet("{id}/messageAttachments")]
    public IActionResult GetMessageAttachments([FromRoute(Name = "id")] string threadKey)
    {
        List<Attachment> staged = pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? list) ? list : new();
        return Ok(staged.Select(a => new { a.Id, a.Name, a.IsImage, a.MimeType, a.Content }));
    }

    /// <summary>
    /// Serves the raw content of an attachment that was sent with a message.
    /// Images are returned as their native mime type; text files as plain text.
    /// Identified by Id, not name — two messages can attach a same-named file (every
    /// clipboard-pasted image used to be called "image.png") and name alone would always resolve to
    /// whichever message happened to appear first in the thread.
    /// </summary>
    [HttpGet("{id}/messageAttachments/{attachmentId}")]
    public IActionResult GetMessageAttachmentContent([FromRoute(Name = "id")] string threadKey, [FromRoute(Name = "attachmentId")] string id)
    {
        if (Llm is null) return StatusCode(503);

        List<ThreadItem> items = FindThread(threadKey)?.History ?? new();
        Attachment? att = items.OfType<Prompt>()
            .SelectMany(i => i.Attachments ?? Enumerable.Empty<Attachment>())
            .FirstOrDefault(a => a.Id == id);
        if (att is null) return NotFound();

        if (att.IsImage)
        {
            byte[] bytes = Convert.FromBase64String(att.Content);
            return File(bytes, att.MimeType ?? "image/jpeg");
        }
        return Content(att.Content, "text/plain");
    }

    /// <summary>
    /// Serves one of the thread's files, by its path inside the thread's folder (its bound project, or its
    /// scratchpad). Pictures and video come back as themselves so they can be shown in the page; anything else
    /// comes back as a download. A path that leaves the folder is refused.
    /// </summary>
    [HttpGet("{id}/files/content")]
    public IActionResult GetFileContent([FromRoute(Name = "id")] string threadKey, [FromQuery] string path)
    {
        if (Llm is null) return StatusCode(503);
        if (string.IsNullOrWhiteSpace(path)) return BadRequest("path is required.");

        string root    = FindThread(threadKey)?.FilesystemRoot ?? Paths.ScratchpadDir(threadKey);
        string rootAbs = Path.GetFullPath(root);
        string absPath = Path.GetFullPath(Path.Combine(rootAbs, path));
        // The trailing separator matters: without it "/x/Foo" would also accept "/x/Foo-secret/...".
        if (!absPath.StartsWith(rootAbs.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || ProjectStore.IsProtectedPath(Path.GetRelativePath(rootAbs, absPath))
            || ProjectStore.PassesThroughLink(rootAbs, absPath))
            return BadRequest("Access denied.");
        if (!System.IO.File.Exists(absPath)) return NotFound();

        string ext = Path.GetExtension(absPath).TrimStart('.').ToLowerInvariant();
        string? shownAs = ext switch { "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "webp" => "image/webp", "mp4" => "video/mp4", "webm" => "video/webm", _ => null };
        return shownAs is not null
            ? PhysicalFile(absPath, shownAs)
            : PhysicalFile(absPath, "application/octet-stream", Path.GetFileName(absPath));
    }

    /// <summary>
    /// Heartbeat sent by the web client while the user is actively composing a message.
    /// Resets the thread's inactivity countdown so Engram doesn't sweep mid-composition.
    /// </summary>
    /// <summary>
    /// Injects a named text attachment into an existing thread (or stages it if the thread
    /// hasn't been created yet). Used by the Electron client to supply the project file tree
    /// and file read results without routing through a user message.
    /// </summary>
    [HttpPost("{id}/systemContext")]
    public IActionResult SetSystemContext([FromRoute(Name = "id")] string threadKey, [FromBody] InjectContextRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Content is null)
            return BadRequest("Name and Content are required.");
        pendingSystemContext[threadKey] = req.Content;
        return Ok();
    }

    [HttpPost("{id}/typing")]
    public IActionResult NotifyTyping([FromRoute(Name = "id")] string threadKey)
    {
        Llm?.NotifyTyping(threadKey);
        return Ok();
    }

    /// <summary>Esc = stop. Cancels the in-flight turn but PRESERVES the partial work (thinking + partial
    /// reply/tools) so nothing is lost — the user's next message is a fresh turn.</summary>
    [HttpPost("{id}/interrupt")]
    public IActionResult InterruptProcessing([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        Llm.Interrupt(threadKey);
        return Ok();
    }

    /// <summary>Mid-turn message = "stop and read this, then continue". The turn keeps running; the message is
    /// folded into Ari's current chain of thought. Returns 409 if the thread isn't streaming (send normally).</summary>
    [HttpPost("{id}/interjections")]
    public IActionResult Interject([FromRoute(Name = "id")] string threadKey, [FromBody] InterjectRequest body)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        pendingMessageAttachments.TryGetValue(threadKey, out List<Attachment>? staged);
        bool folded = Llm.Interject(threadKey, GetUsername(), body?.Text ?? "", staged?.ToList() ?? new());
        if (folded) pendingMessageAttachments.TryRemove(threadKey, out _);
        return folded ? Ok() : Conflict("Thread is not streaming.");
    }

    /// <summary>Close a thread: runs Engram to save it to memory, then deletes it. Fires a threadDeleted event.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> CloseThread([FromRoute(Name = "id")] string threadKey)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");
        // A thread that is not live but is kept by Persistent Threads: closing it again means deleting it for good.
        if (!Llm.Threads.ContainsKey(threadKey) && Llm.DeletePersisted(threadKey)) return Ok();
        bool closed = await Llm.CloseThreadAsync(threadKey);
        return closed ? Ok() : NotFound();
    }

    [HttpPost("{id}/messages")]
    public async Task Stream([FromRoute(Name = "id")] string threadKey, [FromBody] StreamRequest body, CancellationToken cancellationToken)
    {
        body ??= new StreamRequest("");
        string prompt = body.Prompt ?? string.Empty;
        // Sending to an expired thread (Persistent Threads) reopens it first: history, context window and owner restored.
        Llm?.ReopenPersisted(threadKey);
        // Safe mode has two halves: the persistent thread flag (enforced as a hard edit-block in the Coder's
        // tool layer) and the prompt injection that steers her toward read-only planning. Set both. The flag
        // mirrors the toggle each send; CodePipeline clears it when the user approves a plan.
        Llm?.SetSafeMode(threadKey, body?.SafeMode == true);
        if (body?.SafeMode == true)
            prompt = string.IsNullOrWhiteSpace(prompt)
                ? SafeModePromptStore.Get()
                : $"{prompt}\n\n{SafeModePromptStore.Get()}";
        Response.Headers[HeaderNames.ContentType] = "text/event-stream";
        Response.Headers[HeaderNames.CacheControl] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Two ways to read the reply as it is written. The default is the original one (the whole reply so far, with markers in
        // it, then [DONE]); ?format=v2 gives typed JSON events that carry only what is new (see StreamEvents).
        bool v2 = Request.Query["format"] == "v2";
        System.Text.Json.JsonSerializerOptions mvcJson = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>(HttpContext.RequestServices).Value.JsonSerializerOptions;
        StreamEvents events = new(mvcJson);
        async Task Control(string kind, string? message = null, CancellationToken ct = default)
        {
            string line = v2
                ? (kind == "error" ? events.Format("error", new { message }) : events.Format("message.done", new { message = (object?)null }))
                : (kind == "error" ? $"data: [ERROR] {message}\n\n" : "data: [DONE]\n\n");
            await Response.WriteAsync(line, ct);
        }

        if (Llm is null)
        {
            await Control("error", "ARI is not ready yet.", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        // A prompt sent while the model servers are still booting is QUEUED, not rejected (#77): hold it
        // here until every boot-startup server reports Online (the client's typing indicator covers the
        // wait), and only error out if boot genuinely never completes. Prompting mid-boot used to surface
        // raw connection errors to the user.
        // Nothing online at all and nothing coming: the user has not started a server yet (a fresh
        // install ships one that does not boot on startup). Say so plainly instead of waiting four
        // minutes for a boot that was never going to happen, or handing back a raw connection error.
        if (!Llm.Servers.Any(s => s.Status == ARI.LLM.ServerStatus.Online)
            && !Llm.Servers.Any(s => s.BootStartup || s.Status == ARI.LLM.ServerStatus.Starting))
        {
            await Control("error", "No model server is running. Open the control panel, choose the model you want, and start a server.", cancellationToken);
            await Control("done", null, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        if (Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
        {
            DateTime bootDeadline = DateTime.UtcNow.AddMinutes(4);
            while (DateTime.UtcNow < bootDeadline
                   && Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
                await Task.Delay(1000, cancellationToken);
            if (Llm.Servers.Any(s => s.BootStartup && s.Status != ARI.LLM.ServerStatus.Online))
            {
                await Control("error", "ARI's model server has not come online — please try again in a moment.", cancellationToken);
                await Control("done", null, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                return;
            }
        }

        // Safeguard: reject prompts that are clearly too large for the context window.
        // Estimate at 4 chars/token; limit comes from the thread's configured BudgetContext (0 = unconfigured).
        (int _, int contextLimit) = Llm.GetContextStats(threadKey);
        int effectiveLimit    = contextLimit > 0 ? contextLimit : 8000;
        int estimatedTokens   = prompt.Length / 4;
        if (estimatedTokens > effectiveLimit)
        {
            await Response.WriteAsync("data: Your message is too large for me to process. Please attach the content as a file instead.\n\n", cancellationToken);
            await Control("done", null, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        pendingMessageAttachments.TryRemove(threadKey, out List<Attachment>? msgAtts);
        pendingFileNotes.TryRemove(threadKey, out List<string>? fileNotes);
        pendingSystemContext.TryRemove(threadKey, out string? systemContextBlock);

        // Nothing to answer: an empty prompt vanishes from chat history, so a turn started here would
        // answer the previous message instead (sometimes ARI's own). Checks the raw prompt, since safe
        // mode fills an empty one with its own instructions.
        if (string.IsNullOrWhiteSpace(body?.Prompt) && (msgAtts is null || msgAtts.Count == 0) && (fileNotes is null || fileNotes.Count == 0))
        {
            await Control("done", null, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            return;
        }

        // ── Scratchpad wiring ─────────────────────────────────────────────────────
        // If files have been written to this thread's scratchpad, bind it as the
        // FilesystemRoot (if no project is already bound) and inject a file listing so
        // ARI knows to use her file tools.
        string? platformContext = null;
        {
            string scratchpadDir = Paths.ScratchpadDir(threadKey);
            ARI.LLM.Thread? scratchThread = FindThread(threadKey);
            bool hasScratchpad = Directory.Exists(scratchpadDir) && Directory.GetFiles(scratchpadDir, "*", SearchOption.AllDirectories).Length > 0;
            bool noProjectBound = !ThreadProjects.ContainsKey(threadKey);

            if (hasScratchpad && noProjectBound && scratchThread is not null && scratchThread.FilesystemRoot is null)
            {
                scratchThread.FilesystemRoot = scratchpadDir;
                scratchThread.IsBrainVault = false;
                scratchThread.Ct = CancellationToken.None;

                string[] files = Directory.GetFiles(scratchpadDir, "*", SearchOption.AllDirectories);
                StringBuilder listing = new StringBuilder();
                listing.AppendLine("[Workspace files]");
                foreach (string f in files)
                    listing.AppendLine(Path.GetRelativePath(scratchpadDir, f));
                listing.AppendLine("Use list_directory and read_file to inspect these files.");
                platformContext = listing.ToString().TrimEnd();
            }
        }

        if (ThreadProjects.TryGetValue(threadKey, out string? pid))
        {
            Project? project = projectStore.Get(pid);
            if (project is not null)
            {
                StringBuilder ctx = new StringBuilder();
                ctx.AppendLine($"Project: {project.Name}");
                if (!string.IsNullOrWhiteSpace(project.Instructions))
                    ctx.AppendLine().AppendLine(project.Instructions);

                // Filesystem skeleton sent via /systemContext — lives in the system prompt (stable
                // cached prefix) and never appears as a user-visible thread attachment.
                if (systemContextBlock is not null)
                {
                    ctx.AppendLine().AppendLine(systemContextBlock);
                    ctx.AppendLine("Use list_directory to explore subdirectories and read_file to read files.");
                }
                // No tree from the desktop app (a server-side project, or the web/phone UI) — build it here, so
                // every project thread starts knowing what's in the folder.
                else if (project.RootPath is { } overviewRoot && Directory.Exists(overviewRoot))
                    ctx.AppendLine().AppendLine(ARI.LLM.ProjectOverview.Build(overviewRoot));

                // If the project folder is or contains git repos, tell ARI to pull before working.
                // Path.Exists rather than Directory.Exists: a submodule's .git is a file.
                if (project.RootPath is { } root && Directory.Exists(root))
                {
                    bool hasRepos = Path.Exists(Path.Combine(root, ".git"))
                        || Directory.EnumerateDirectories(root).Any(d => Path.Exists(Path.Combine(d, ".git")));
                    if (hasRepos)
                        ctx.AppendLine()
                           .AppendLine("This project contains git repositories. Load git_tools to get the `git` tool, which lists the available repos automatically. Before working in any repo, run git({repo}, \"fetch\") then git({repo}, \"status\") to check for upstream changes, and git({repo}, \"pull\") to update. Always work on up-to-date code.");
                }

                platformContext = ctx.ToString().TrimEnd();

                ARI.LLM.Thread? boundThread = FindThread(threadKey);

                // Force code pipeline before the classifier runs (first message only)
                bool isFirstMessage = boundThread?.History.Count is null or 0;
                if (isFirstMessage && ProjectCodeRules.RunsAsCode(project))
                    Llm.ForceCodeThread(threadKey);

                // The project's name rides on the thread whether or not its files are on this server's disk, so
                // Engram can file a code thread's decisions on the right Projects/ note.
                if (boundThread is not null) boundThread.ProjectName = project.Name;

                // Bind FilesystemRoot on the thread every message (idempotent) so filesystem_tools/coding_tools
                // resolve correctly. Local path (Electron) is preferred and set later via effectiveLocalPath;
                // RootPath here is the server-side fallback for web sessions.
                if (boundThread is not null && project is { RootPath: { } serverRoot })
                {
                    boundThread.FilesystemRoot   = serverRoot;
                    boundThread.IsBrainVault  = false;
                    boundThread.Ct            = CancellationToken.None;
                }

            }
        }

        // Append file-upload notes to platformContext so ARI knows to read them.
        if (fileNotes is { Count: > 0 })
        {
            string noteLines = fileNotes.Count == 1
                ? $"[{fileNotes[0]} was uploaded to your workspace — use read_file to inspect it.]"
                : "[The following files were uploaded to your workspace — use read_file to inspect them: "
                  + string.Join(", ", fileNotes) + "]";
            platformContext = string.IsNullOrEmpty(platformContext)
                ? noteLines
                : platformContext + "\n" + noteLines;
        }

        // Heartbeat: while the model processes (prompt-processing a large context, running tools, thinking) no
        // content deltas are produced, so the SSE connection sits idle. Reverse proxies/tunnels sitting in
        // front of ARI often cap idle connections (commonly ~100s) and cut it, showing "[connection error]".
        // An SSE comment line (":") every 15s keeps the connection warm; the client ignores comment lines. All writes to the response
        // body are serialised through writeLock so the heartbeat and the content callback never issue
        // concurrent writes (which would corrupt the stream).
        using SemaphoreSlim writeLock = new(1, 1);
        using CancellationTokenSource heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task WriteEventAsync(string payload)
        {
            // Use None so a client disconnect doesn't throw — LLM must keep running.
            await writeLock.WaitAsync(CancellationToken.None);
            try
            {
                if (cancellationToken.IsCancellationRequested) return;
                await Response.WriteAsync(payload, CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch { /* swallow broken pipe — client navigated away */ }
            finally { writeLock.Release(); }
        }

        Task heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
                    await WriteEventAsync(": keepalive\n\n");
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch { /* a broken pipe here is harmless — the main path reports the error */ }
        });

        try
        {
            string username = GetUsername();
            // Pass CancellationToken.None so HTTP client disconnect does NOT cancel the LLM.
            // The LLM runs to completion; explicit cancel via POST /interrupt still works
            // because LLMModule.Cancel() cancels the thread's internal CTS directly.
            // A ServerFs project's root is server-managed — resolve it from the bound project when the
            // client didn't send one explicitly (RemoteFs keeps relying on the client-sent LocalPath,
            // sourced from the desktop app's own per-device folder store, same as before).
            // Only an admin may name a folder on this machine; for anyone else the path is ignored and only the bound project is used.
            string? effectiveLocalPath = string.IsNullOrWhiteSpace(body.LocalPath) || !User.IsInRole(ARI.API.Auth.Roles.Admin) ? null : body.LocalPath;
            if (effectiveLocalPath is null
                && ThreadProjects.TryGetValue(threadKey, out string? boundProjectId)
                && projectStore.Get(boundProjectId) is { RootPath: { } rootPath })
                effectiveLocalPath = rootPath;

            // Re-evaluated every message from the caller's own login, so the flag can't outlive a role change.
            // Also marks whose thread it is, so a guest's chat never gets the Master's memories.
            ARI.LLM.Thread chatThread = GetOrCreateThread(threadKey);
            chatThread.IsAdminChat = User.IsInRole(ARI.API.Auth.Roles.Admin);

            if (v2) await WriteEventAsync(events.Format("message.start", new { threadId = threadKey }));
            await Llm.PromptStreaming(threadKey, prompt, username, platformContext, async accumulated =>
            {
                if (v2)
                {
                    foreach (string evt in events.FromAccumulated(accumulated)) await WriteEventAsync(evt);
                    return;
                }
                string escaped = accumulated.Replace("\n", "\\n").Replace("\r", "");
                await WriteEventAsync($"data: {escaped}\n\n");
            }, CancellationToken.None, messageAttachments: msgAtts,
               localPath: effectiveLocalPath);
            if (v2)
            {
                // The wrap-up: the whole finished reply, exactly as GET /threads/{id}/messages returns it.
                ARI.LLM.Response? finished = chatThread.History.OfType<ARI.LLM.Response>().LastOrDefault();
                await WriteEventAsync(events.Format("message.done", new { message = finished is null ? null : JsonSerializer.SerializeToNode<ThreadItem>(finished, mvcJson) }));
            }
            else await WriteEventAsync("data: [DONE]\n\n");
        }
        catch (OperationCanceledException)
        {
            await WriteEventAsync(v2 ? events.Format("message.cancelled", new { }) : "data: [CANCELLED]\n\n");
        }
        catch (Exception ex)
        {
            // The details go to the server log; the person sees only that it failed (and admins get the reason).
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(HttpContext.RequestServices).CreateLogger("ARI.Threads").LogError(ex, "Reply failed on thread {Thread}", threadKey);
            string failure = User.IsInRole(ARI.API.Auth.Roles.Admin) ? ex.Message : "Something went wrong while replying.";
            await WriteEventAsync(v2 ? events.Format("error", new { message = failure }) : $"data: [ERROR] {failure}\n\n");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { /* ignore */ }
        }
    }
}

public record StreamRequest(string Prompt, string? LocalPath = null, bool SafeMode = false);
public record InterjectRequest(string? Text);
public record ApprovalAnswer(bool Allow);
public record PromoteToProjectRequest(string Name, string? Category = null);
public record CommandRequest(string? ThreadKey, string Input);
public record NewThreadRequest(string? ProjectId, bool Desktop = false, string? Pipeline = null);
public record InjectContextRequest(string Name, string Content);
public record ThreadEntry(string Key, string? AgentName, bool IsInternal, DateTime LastMessageAt, int MessageCount, string State = "active", bool IsCodeMode = false, string? ProjectName = null, string? ProjectId = null, string Pipeline = "dialogue", string? Title = null, string? OwnerName = null, string? ParentKey = null, string? Label = null);
