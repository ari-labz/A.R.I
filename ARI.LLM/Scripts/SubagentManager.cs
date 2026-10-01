using ARI.Common;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace ARI.LLM;

/// <summary>
/// Runs and tracks subagents for each thread. spawn_agent starts one in the background and returns at once;
/// wait_for_agent holds the parent's turn open (no generation) until its agents finish, the user interjects,
/// or the wait times out. A result nobody waited for is handed to the parent at its next step boundary, so
/// it arrives mid-turn or at the start of the next turn, whichever comes first.
/// </summary>
internal static class SubagentManager
{
    internal const int      MaxRunningPerThread = 2;
    internal const int      DefaultWaitSeconds  = 300;
    internal const int      MaxWaitSeconds      = 600;
    internal static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(10);
    private  static readonly TimeSpan WaitPoll   = TimeSpan.FromMilliseconds(250);

    /// <summary>The agent that runs subagent tasks. Null when not configured, which hides the tools.</summary>
    internal static Subagent? Agent { get; set; }

    // Tools a subagent may never get, whatever the parent has: control flow that belongs to the parent's own
    // turn, and anything that could widen its toolset (request_tools would let it load groups its parent
    // never had, sidestepping the subset rule).
    private static readonly HashSet<string> NeverInherited = new(StringComparer.Ordinal)
    {
        "spawn_agent", "wait_for_agent", "cancel_agent", "list_tools", "request_tools",
        "plan_proposed", "start_build", "replan", "wake",
    };

    // Given when spawn_agent names no tools: look, don't touch.
    private static readonly string[] DefaultTools =
    {
        "read_file", "preview_file", "list_directory", "search_files", "find_files",
        "search_web", "fetch_page", "search_brain", "recall_memory", "get_time",
    };

    internal sealed class Run
    {
        internal required int                     Id;
        internal required string                  Title;
        internal required Task<string>            Task;
        internal required CancellationTokenSource Cts;
        internal required DateTime                Started;
        internal bool                             Delivered;
        internal bool                             Cancelled;

        internal bool Finished => Task.IsCompleted;

        internal string Report()
        {
            string name = $"Agent {Id} \"{Title}\"";
            if (!Task.IsCompleted) return $"{name} is still running.";
            if (Cancelled)         return $"{name} was cancelled.";
            if (Task.IsFaulted || Task.IsCanceled)
                return $"{name} failed: {(Task.IsCanceled ? $"ran past its {MaxRunTime.TotalMinutes:0}-minute limit" : Task.Exception?.GetBaseException().Message)}";
            return $"{name} finished:\n{Task.Result.Trim()}";
        }
    }

    private sealed class ThreadRuns
    {
        internal readonly List<Run> Runs = new();
        internal int NextId = 1;
    }

    private static readonly ConcurrentDictionary<string, ThreadRuns> byThread = new();

    /// <summary>Subagents are offered to conversations, not to internal threads (no nesting) or Discord
    /// server channels (anyone there could direct them).</summary>
    internal static bool AvailableFor(Thread t) =>
        Agent is not null && !t.Internal && !t.Key.StartsWith("guild:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts a subagent on its own internal thread with a title, a task prompt and optional context (it
    /// starts with a blank memory, so context carries whatever it needs from this conversation). Returns the run,
    /// or an error message.</summary>
    internal static (Run? Run, string Message) Spawn(Thread parent, string title, string task, string? context, IReadOnlyList<string>? toolNames)
    {
        if (Agent is not { } agent) return (null, "Subagents aren't configured on this server.");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(task)) return (null, "Give the subagent a title and a prompt.");

        ThreadRuns state = byThread.GetOrAdd(parent.Key, _ => new ThreadRuns());
        int id;
        lock (state)
        {
            int running = state.Runs.Count(r => !r.Finished);
            if (running >= MaxRunningPerThread)
                return (null, $"{running} agents are already running here (limit {MaxRunningPerThread}). Wait for one, or cancel one.");
            id = state.NextId++;
        }

        Thread child = new Thread(ThreadPipeline.Dialogue, $"subagent:{parent.Key}:{id}") { Internal = true };
        (List<string> given, List<string> refused) = InheritTools(parent, child, toolNames);

        CancellationTokenSource cts = new(MaxRunTime);
        Run run = new()
        {
            Id      = id,
            Title   = title,
            Cts     = cts,
            Started = DateTime.Now,
            Task    = System.Threading.Tasks.Task.Run(() => agent.RunTask(child, title, task, context, cts.Token)),
        };
        lock (state) state.Runs.Add(run);
        // A result nobody is waiting for wakes the parent if it's idle by then.
        run.Task.ContinueWith(_ => ParentReports.Nudge?.Invoke(parent.Key), TaskScheduler.Default);

        Shared.Logger.LogInformation("[Subagents] ({Thread}) started agent {Id} \"{Title}\" with [{Tools}]: {Task}",
            parent.Key, id, title, string.Join(", ", given), task);

        StringBuilder msg = new($"Agent {id} \"{title}\" started with tools: {(given.Count > 0 ? string.Join(", ", given) : "none")}.");
        if (refused.Count > 0)
            msg.Append($" Not given (not loaded in this conversation, or never available to subagents): {string.Join(", ", refused)}.");
        msg.Append(" Call wait_for_agent when you need its result.");
        return (run, msg.ToString());
    }

    /// <summary>Copies the allowed tools from the parent's live tool table onto the child. A subagent can never
    /// hold a tool its parent doesn't currently have.</summary>
    private static (List<string> Given, List<string> Refused) InheritTools(Thread parent, Thread child, IReadOnlyList<string>? requested)
    {
        bool explicitList = requested is { Count: > 0 };
        IEnumerable<string> wanted = explicitList ? requested! : DefaultTools;
        List<string> given = new(), refused = new();
        foreach (string name in wanted.Distinct(StringComparer.Ordinal))
        {
            if (!NeverInherited.Contains(name) && parent.tools.TryGetValue(name, out var entry))
            {
                child.tools[name] = entry;
                given.Add(name);
            }
            else if (explicitList) refused.Add(name);   // defaults the parent lacks are skipped quietly
        }
        return (given, refused);
    }

    /// <summary>The holding pattern. Returns when every listed agent has finished, the user interjects, the
    /// wait times out, or the parent turn is cancelled. Reports each agent's state either way.</summary>
    internal static async Task<string> Wait(Thread parent, IReadOnlyList<int>? ids, int maxSeconds, CancellationToken ct)
    {
        if (!byThread.TryGetValue(parent.Key, out ThreadRuns? state)) return "No agents have been started here.";
        List<Run> targets;
        lock (state)
            targets = ids is { Count: > 0 }
                ? state.Runs.Where(r => ids.Contains(r.Id)).ToList()
                : state.Runs.Where(r => !r.Delivered).ToList();
        if (targets.Count == 0) return "No matching agents to wait for.";

        int seconds = maxSeconds <= 0 ? DefaultWaitSeconds : Math.Min(maxSeconds, MaxWaitSeconds);
        DateTime until = DateTime.Now.AddSeconds(seconds);
        string? stoppedBecause = null;
        Task all = System.Threading.Tasks.Task.WhenAll(targets.Select(r => (Task)r.Task));

        while (!all.IsCompleted)
        {
            if (parent.HasInterjections) { stoppedBecause = "The user sent a message while you were waiting. Read it; you can wait again afterwards."; break; }
            if (ParentReports.HasAny(parent.Key)) { stoppedBecause = "A conversation you opened reported back while you were waiting. Read it; you can wait again afterwards."; break; }
            if (DateTime.Now >= until)   { stoppedBecause = $"Stopped waiting after {seconds}s. Wait again, or carry on and the results will reach you when they're ready."; break; }
            if (ct.IsCancellationRequested) { stoppedBecause = "Your turn was stopped."; break; }
            try { await System.Threading.Tasks.Task.WhenAny(all, System.Threading.Tasks.Task.Delay(WaitPoll, ct)); }
            catch (OperationCanceledException) { }
        }

        StringBuilder sb = new();
        if (stoppedBecause is not null) sb.AppendLine(stoppedBecause).AppendLine();
        lock (state)
        {
            foreach (Run r in targets)
            {
                sb.AppendLine(r.Report()).AppendLine();
                if (r.Finished) r.Delivered = true;
            }
            Prune(state);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Titles of the agents a wait with these ids would cover (all undelivered when ids is empty).</summary>
    internal static List<string> Titles(Thread parent, IReadOnlyList<int>? ids)
    {
        if (!byThread.TryGetValue(parent.Key, out ThreadRuns? state)) return new();
        lock (state)
            return (ids is { Count: > 0 } ? state.Runs.Where(r => ids.Contains(r.Id)) : state.Runs.Where(r => !r.Delivered))
                .Select(r => r.Title).ToList();
    }

    internal static string Cancel(Thread parent, int id)
    {
        if (!byThread.TryGetValue(parent.Key, out ThreadRuns? state)) return $"No agent {id} here.";
        lock (state)
        {
            Run? r = state.Runs.FirstOrDefault(x => x.Id == id);
            if (r is null)   return $"No agent {id} here.";
            if (r.Finished)  return $"Agent {id} \"{r.Title}\" had already finished.";
            r.Cancelled = true;
            r.Delivered = true;   // nothing left to hand over
            r.Cts.Cancel();
            return $"Agent {id} \"{r.Title}\" cancelled.";
        }
    }

    internal static bool HasUnwaited(Thread parent)
    {
        if (!byThread.TryGetValue(parent.Key, out ThreadRuns? state)) return false;
        lock (state) return state.Runs.Any(r => r.Finished && !r.Delivered);
    }

    /// <summary>Results that finished without anyone waiting, marked delivered as they're taken. Folded into
    /// the parent at its next step boundary. Null when there's nothing new.</summary>
    internal static string? TakeUnwaitedResults(Thread parent)
    {
        if (!byThread.TryGetValue(parent.Key, out ThreadRuns? state)) return null;
        List<string> reports = new();
        lock (state)
        {
            foreach (Run r in state.Runs.Where(r => r.Finished && !r.Delivered))
            {
                reports.Add(r.Report());
                r.Delivered = true;
            }
            Prune(state);
        }
        return reports.Count == 0
            ? null
            : "[Results from agents you started, which finished while you weren't waiting]\n" + string.Join("\n\n", reports);
    }

    // Drop delivered runs; ids keep counting up so a stale id can't point at a new agent.
    private static void Prune(ThreadRuns state) => state.Runs.RemoveAll(r => r.Finished && r.Delivered);
}
