using System.Collections.Concurrent;

namespace ARI.LLM;

/// <summary>
/// Reports from sub-threads (a DM conversation ARI opened, a subagent) to the thread that started them. A report
/// reaches the parent however it is placed: a wait that's holding for it returns it, a running turn folds it in at
/// its next step, and an idle parent is woken with a new turn so it can act on it (reply in its chat, post in its
/// Discord channel). <see cref="Nudge"/> is the wake hook, set by <see cref="LLMModule"/>.
/// </summary>
internal static class ParentReports
{
    internal sealed record Report(string FromKey, string Text);

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<Report>> inbox = new();

    /// <summary>Wakes a parent thread if it's idle and has something pending. Set by LLMModule.</summary>
    internal static Action<string>? Nudge { get; set; }

    internal static void Send(string parentKey, string fromKey, string text)
    {
        inbox.GetOrAdd(parentKey, _ => new()).Enqueue(new Report(fromKey, text));
        Nudge?.Invoke(parentKey);
    }

    internal static bool HasAny(string parentKey) => inbox.TryGetValue(parentKey, out var q) && !q.IsEmpty;

    /// <summary>Takes the first report from one sub-thread, leaving the rest. For a wait holding on that sub-thread.</summary>
    internal static string? TakeFrom(string parentKey, string fromKey)
    {
        if (!inbox.TryGetValue(parentKey, out var q)) return null;
        lock (q)
        {
            List<Report> all = new();
            while (q.TryDequeue(out Report? r)) all.Add(r);
            Report? hit = all.FirstOrDefault(r => r.FromKey == fromKey);
            foreach (Report r in all) if (!ReferenceEquals(r, hit)) q.Enqueue(r);
            return hit?.Text;
        }
    }

    /// <summary>Every pending report for this parent, as one message. Null when there are none.</summary>
    internal static string? TakeAll(string parentKey)
    {
        if (!inbox.TryGetValue(parentKey, out var q)) return null;
        List<string> texts = new();
        lock (q)
            while (q.TryDequeue(out Report? r)) texts.Add(r.Text);
        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }
}
