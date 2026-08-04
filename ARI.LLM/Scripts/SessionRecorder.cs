using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>Recording knobs. Lives in AriConfig so a work session can be captured in full and a
/// demo machine can turn the whole thing off without a rebuild.</summary>
public sealed class RecordingConfig
{
    /// <summary>Master switch. When false, SessionRecorder is inert and costs nothing.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Embed the complete request body (system prompt + every message + tool schemas) on each
    /// step. Off still records per-message digests and sizes, which is enough for cache-break analysis
    /// but not enough to replay a run verbatim.</summary>
    public bool IncludeFullRequests { get; init; } = true;

    /// <summary>Include tool results verbatim. Off records only the name and result length.</summary>
    public bool IncludeToolResults { get; init; } = true;

    /// <summary>Date folders older than this are deleted at startup. 0 disables cleanup.</summary>
    public int RetentionDays { get; init; } = 30;
}

/// <summary>
/// Append-only JSONL record of every LLM round-trip ARI makes — every agent, every turn, every step.
/// One file per thread per day under Logs/Sessions/&lt;date&gt;/, plus an index.jsonl tying an agent run
/// back to the user prompt that caused it.
///
/// Purpose: reconstruct a real session days later — what the model saw, what it sent back, how long
/// each phase took and which message broke the prefix cache — without needing the process still running.
/// Best-effort throughout: any failure here is swallowed so recording can never disrupt a conversation.
/// </summary>
public static class SessionRecorder
{
    private static RecordingConfig config = new() { Enabled = false };
    private static string          processId = "";

    private static readonly ConcurrentDictionary<string, object> FileGates = new();
    private static readonly AsyncLocal<string?>                  AmbientExchange = new();

    // Most recent run per thread key, so a note written after the agent's Prompt() has returned still
    // lands in that run's file with a continuous sequence. Bounded — internal worker threads use a fresh
    // GUID key per call, so this would otherwise grow without limit for the life of the process.
    private const int RUN_CACHE_LIMIT = 512;
    private static readonly ConcurrentDictionary<string, Run> RunsByThread = new();
    private static readonly ConcurrentQueue<string>           RunCacheOrder = new();

    private static bool Off => !config.Enabled;

    /// <summary>One agent run — a single <see cref="Agent.Prompt"/> call, spanning every step it takes.
    /// Carries the destination file and the exchange it belongs to so sub-agent runs can be tied back
    /// to the user prompt that triggered them.</summary>
    internal sealed class Run
    {
        internal required string Id         { get; init; }
        internal required string ExchangeId { get; init; }
        internal required string RootThread { get; init; }
        internal required string Agent      { get; init; }
        internal required string ThreadKey  { get; init; }
        internal required string File       { get; init; }

        private int seq;
        internal int NextSeq() => Interlocked.Increment(ref seq);

        /// <summary>Digests of the last request's messages — lets each request name the exact index at
        /// which the prefix diverged, which is where the server had to re-prefill. Carried over from the
        /// thread's previous run, because llama-server's KV cache outlives a single agent run: the reason
        /// turn 5 re-read 8k tokens is usually something turn 4 left behind.</summary>
        internal string[]? PreviousDigests;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public static void Configure(RecordingConfig cfg)
    {
        config    = cfg;
        processId = Guid.NewGuid().ToString("N")[..8];

        if (Off) { Shared.Logger.LogInformation("[SessionRecorder] disabled."); return; }

        try
        {
            Directory.CreateDirectory(Paths.Sessions);
            Prune();
            AppendIndex(new Dictionary<string, object?>
            {
                ["ts"]      = Stamp(),
                ["event"]   = "process_start",
                ["proc"]    = processId,
                ["devmode"] = Shared.DevMode,
            });
            Shared.Logger.LogInformation("[SessionRecorder] recording to {Path} (full requests: {Full}, retention: {Days}d)",
                Paths.Sessions, config.IncludeFullRequests, config.RetentionDays);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SessionRecorder] failed to initialise: {Error}", ex.Message);
        }
    }

    /// <summary>Deletes date folders older than the retention window.</summary>
    private static void Prune()
    {
        if (config.RetentionDays <= 0) return;

        DateTime cutoff = DateTime.Today.AddDays(-config.RetentionDays);
        foreach (string dir in Directory.GetDirectories(Paths.Sessions))
        {
            if (DateTime.TryParse(Path.GetFileName(dir), out DateTime day) && day < cutoff)
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Exchange scope ────────────────────────────────────────────────────────

    /// <summary>Opens the correlation scope for one user prompt. Every agent run started underneath it —
    /// Memory's recall, Context's summary, the dialogue agent itself — inherits the same exchange id, so
    /// the whole fan-out can be reassembled from separate files later. Returns null when recording is off.</summary>
    internal static IDisposable? BeginExchange(string threadKey, string username, string prompt)
    {
        if (Off) return null;

        string  id       = Guid.NewGuid().ToString("N")[..12];
        string? previous = AmbientExchange.Value;
        AmbientExchange.Value = id;

        AppendIndex(new Dictionary<string, object?>
        {
            ["ts"]       = Stamp(),
            ["event"]    = "exchange_start",
            ["proc"]     = processId,
            ["exchange"] = id,
            ["thread"]   = threadKey,
            ["user"]     = username,
            ["prompt"]   = prompt,
        });

        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientExchange.Value = previous;
    }

    // ── Run lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Starts recording one agent run. An agent invoked outside any exchange (Engram sweeping a
    /// dormant thread, the scheduler) opens an exchange of its own so it still lands in the index.</summary>
    internal static Run? BeginRun(string agent, Thread thread, string prompt, int maxTokens, int thinkBudget)
    {
        if (Off) return null;

        try
        {
            string exchangeId = AmbientExchange.Value ?? "solo-" + Guid.NewGuid().ToString("N")[..8];
            string rootThread = RootOf(thread);

            // Internal worker threads carry a per-call GUID key, so prefixing the agent keeps their files
            // identifiable at a glance; user-facing threads are named by thread key alone and accumulate
            // every exchange of that day in one file.
            string stem = thread.Internal ? $"{agent}_{thread.Key}" : thread.Key;
            string file = Path.Combine(DayDir(), $"{Sanitize(stem)}.jsonl");

            Run run = new()
            {
                Id         = Guid.NewGuid().ToString("N")[..8],
                ExchangeId = exchangeId,
                RootThread = rootThread,
                Agent      = agent,
                ThreadKey  = thread.Key,
                File       = file,
                PreviousDigests = RunsByThread.TryGetValue(thread.Key, out Run? prior) ? prior.PreviousDigests : null,
            };

            AppendIndex(new Dictionary<string, object?>
            {
                ["ts"]       = Stamp(),
                ["event"]    = "run_start",
                ["proc"]     = processId,
                ["exchange"] = exchangeId,
                ["run"]      = run.Id,
                ["root"]     = rootThread,
                ["agent"]    = agent,
                ["thread"]   = thread.Key,
                ["file"]     = Path.GetFileName(file),
            });

            Write(run, "run_start", new Dictionary<string, object?>
            {
                ["proc"]         = processId,
                ["pipeline"]     = thread.Pipeline.ToString(),
                ["internal"]     = thread.Internal,
                ["label"]        = thread.Label,
                ["prompt"]       = prompt,
                ["max_tokens"]   = maxTokens,
                ["think_budget"] = thinkBudget,
            });

            Remember(run);
            return run;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SessionRecorder] failed to open run for {Agent}: {Error}", agent, ex.Message);
            return null;
        }
    }

    internal static void EndRun(Run? run, string? responseText, Exception? error = null) => Write(run, "run_end", new Dictionary<string, object?>
    {
        ["ok"]       = error is null,
        ["response"] = responseText,
        ["error"]    = error?.Message,
        ["error_type"] = error?.GetType().Name,
    });

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>One request body sent to /v1/chat/completions. <paramref name="messages"/> is digested
    /// message-by-message so a later diff can point at the first index that changed — the cache break.</summary>
    internal static void Request(
        Run?          run,
        int           step,
        string        json,
        List<object>  messages,
        object[]?     toolSchemas,
        bool          dynamicInjected,
        int           maxTokens)
    {
        if (run is null || Off) return;

        try
        {
            string[] digests = new string[messages.Count];
            int[]    sizes   = new int[messages.Count];
            string[] roles   = new string[messages.Count];

            for (int i = 0; i < messages.Count; i++)
            {
                string body = JsonSerializer.Serialize(messages[i]);
                digests[i] = Digest(body);
                sizes[i]   = body.Length;
                roles[i]   = messages[i].GetType().GetProperty("role")?.GetValue(messages[i]) as string ?? "?";
            }

            int? divergedAt = FirstDifference(run.PreviousDigests, digests);
            run.PreviousDigests = digests;

            Dictionary<string, object?> payload = new()
            {
                ["step"]             = step,
                ["max_tokens"]       = maxTokens,
                ["dynamic_injected"] = dynamicInjected,
                ["tools"]            = toolSchemas is null ? null : ToolNames(toolSchemas),
                ["bytes"]            = json.Length,
                ["msg_count"]        = messages.Count,
                ["msg_roles"]        = roles,
                ["msg_sizes"]        = sizes,
                ["msg_digests"]      = digests,
                // Index of the first message that changed since the last request on this thread, so
                // msg_sizes from here on is what had to be re-prefilled. -1 = nothing changed (full
                // cache reuse expected); null = no earlier request to compare against.
                ["prefix_diverged_at"] = divergedAt,
            };

            if (config.IncludeFullRequests)
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                payload["body"] = doc.RootElement.Clone();
            }

            Write(run, "request", payload);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SessionRecorder] failed to record request: {Error}", ex.Message);
        }
    }

    /// <summary>One completed model response within a run. Token counts and clock buckets are per-step
    /// deltas, so summing a file's response events reproduces the run totals.</summary>
    internal static void Response(
        Run?     run,
        int      step,
        string?  text,
        string?  reasoning,
        string?  finishReason,
        int      promptTokens,
        int      prefilledTokens,
        int      completionTokens,
        double   prefillSeconds,
        double   thinkingSeconds,
        double   typingSeconds,
        double   prefillTokPerSec,
        IEnumerable<string> pendingToolCalls) => Write(run, "response", new Dictionary<string, object?>
    {
        ["step"]          = step,
        ["finish_reason"] = finishReason,
        ["text"]          = text,
        ["reasoning"]     = reasoning,
        ["tool_calls"]    = pendingToolCalls.ToArray(),
        ["usage"] = new Dictionary<string, object?>
        {
            ["prompt"]     = promptTokens,
            // Tokens the server actually re-read; prompt - prefilled was served from KV cache. -1 = not reported.
            ["prefilled"]  = prefilledTokens,
            ["completion"] = completionTokens,
        },
        ["timings"] = new Dictionary<string, object?>
        {
            ["prefill_s"]     = Round(prefillSeconds),
            ["thinking_s"]    = Round(thinkingSeconds),
            ["typing_s"]      = Round(typingSeconds),
            ["prefill_tok_s"] = Round(prefillTokPerSec),
        },
    });

    internal static void ToolCall(Run? run, int step, string callId, string name, string argsJson) => Write(run, "tool_call", new Dictionary<string, object?>
    {
        ["step"]    = step,
        ["call_id"] = callId,
        ["name"]    = name,
        ["args"]    = argsJson,
    });

    internal static void ToolResult(Run? run, int step, string callId, string name, string result) => Write(run, "tool_result", new Dictionary<string, object?>
    {
        ["step"]    = step,
        ["call_id"] = callId,
        ["name"]    = name,
        ["chars"]   = result.Length,
        ["result"]  = config.IncludeToolResults ? result : null,
    });

    /// <summary>Records non-LLM work that shaped a run — Memory's search terms and candidate scoring,
    /// Engram's classification and commit count. This is what the old run-log "Run summary" block held,
    /// as queryable fields rather than prose.</summary>
    internal static void Note(Run? run, string label, Dictionary<string, object?> facts)
    {
        if (run is null || Off) return;

        Dictionary<string, object?> payload = new(facts) { ["label"] = label };
        Write(run, "note", payload);
    }

    /// <summary>Note written without a run handle. Appends to that thread's most recent run when there is
    /// one (the usual case — a summary written once the agent's Prompt has returned); otherwise opens a
    /// file of its own, which is what records the bail-outs that never reach the model at all.</summary>
    internal static void StandaloneNote(string agent, string threadKey, string label, Dictionary<string, object?> facts)
    {
        if (Off) return;

        try
        {
            if (RunsByThread.TryGetValue(threadKey, out Run? existing))
            {
                Note(existing, label, facts);
                return;
            }

            Run scratch = new()
            {
                Id         = Guid.NewGuid().ToString("N")[..8],
                ExchangeId = AmbientExchange.Value ?? "solo-" + Guid.NewGuid().ToString("N")[..8],
                RootThread = threadKey,
                Agent      = agent,
                ThreadKey  = threadKey,
                File       = Path.Combine(DayDir(), $"{Sanitize($"{agent}_{threadKey}")}.jsonl"),
            };
            Note(scratch, label, facts);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SessionRecorder] failed to record note for {Agent}: {Error}", agent, ex.Message);
        }
    }

    private static void Remember(Run run)
    {
        RunsByThread[run.ThreadKey] = run;
        RunCacheOrder.Enqueue(run.ThreadKey);

        while (RunCacheOrder.Count > RUN_CACHE_LIMIT && RunCacheOrder.TryDequeue(out string? stale))
            if (!RunCacheOrder.Contains(stale)) RunsByThread.TryRemove(stale, out _);
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    private static void Write(Run? run, string eventName, Dictionary<string, object?> payload)
    {
        if (run is null || Off) return;

        try
        {
            Dictionary<string, object?> line = new()
            {
                ["ts"]       = Stamp(),
                ["seq"]      = run.NextSeq(),
                ["run"]      = run.Id,
                ["exchange"] = run.ExchangeId,
                ["root"]     = run.RootThread,
                ["thread"]   = run.ThreadKey,
                ["agent"]    = run.Agent,
                ["event"]    = eventName,
            };
            foreach ((string key, object? value) in payload) line[key] = value;

            Append(run.File, line);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[SessionRecorder] failed to write {Event} for {Agent}: {Error}", eventName, run.Agent, ex.Message);
        }
    }

    private static void AppendIndex(Dictionary<string, object?> line)
    {
        try { Append(Path.Combine(DayDir(), "index.jsonl"), line); }
        catch (Exception ex) { Shared.Logger.LogWarning("[SessionRecorder] failed to write index: {Error}", ex.Message); }
    }

    // One append per event rather than a buffered writer: these fire a few times per second at most,
    // and an unbuffered file survives a crash — which is the whole point of recording a session.
    private static void Append(string file, Dictionary<string, object?> line)
    {
        string json = JsonSerializer.Serialize(line, SerializerOptions);
        lock (FileGates.GetOrAdd(file, _ => new object()))
            File.AppendAllText(file, json + "\n");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DayDir()
    {
        string dir = Path.Combine(Paths.Sessions, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Stamp() => DateTime.Now.ToString("O");

    private static string RootOf(Thread thread)
    {
        Thread root = thread;
        while (root.Parent is { } parent) root = parent;
        return root.Key;
    }

    private static string[] ToolNames(object[] schemas)
    {
        List<string> names = new(schemas.Length);
        foreach (object schema in schemas)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(schema));
                names.Add(doc.RootElement.TryGetProperty("function", out JsonElement fn)
                          && fn.TryGetProperty("name", out JsonElement nm)
                    ? nm.GetString() ?? "?"
                    : "?");
            }
            catch { names.Add("?"); }
        }
        return names.ToArray();
    }

    /// <summary>Index of the first message whose digest changed since the previous request, -1 when the
    /// prefix is unchanged, or null when there is no previous request to compare against. Everything from
    /// that index on had to be re-prefilled.</summary>
    private static int? FirstDifference(string[]? previous, string[] current)
    {
        if (previous is null) return null;
        int shared = Math.Min(previous.Length, current.Length);
        for (int i = 0; i < shared; i++)
            if (previous[i] != current[i]) return i;
        return current.Length > previous.Length ? previous.Length : -1;
    }

    /// <summary>FNV-1a 64. Only ever compared against another digest of the same function, so a
    /// non-cryptographic hash is the right trade — it keeps a per-message digest cheap enough to run
    /// on every message of every step.</summary>
    private static string Digest(string text)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime  = 1099511628211;

        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash.ToString("x16");
    }

    private static double Round(double value) => Math.Round(value, 3);

    private static string Sanitize(string name)
    {
        StringBuilder sb = new(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c is ':' or ' ' ? '_' : c);
        string s = sb.ToString();
        return s.Length <= 120 ? s : s[..120];
    }
}
