using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : Model, IDisposable
{
    private readonly Dialogue dialogue;
    private readonly BrainService brain;
    private readonly Context? context;

    private readonly Dictionary<string, DateTime> lastEngramRun = new();
    private readonly SemaphoreSlim engramLock = new(1, 1);
    private readonly Timer? sweepTimer;
    private readonly int fetchDepth;
    private TimeSpan sweepInterval;

    internal Engram(ModelConfig config, Dialogue dialogue, BrainService brain, Context? context, int sweepIntervalMinutes, int fetchDepth = 7) : base(config)
    {
        this.dialogue   = dialogue;
        this.brain      = brain;
        this.context    = context;
        this.fetchDepth = fetchDepth;

        dialogue.ThreadBufferFull += (threadKey, history) =>
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => RunEngramAsync(threadKey, history, "chat buffer"));

        if (sweepIntervalMinutes > 0)
        {
            sweepInterval = TimeSpan.FromMinutes(sweepIntervalMinutes);
            sweepTimer    = new Timer(_ => _ = SweepThreadsAsync(), null, sweepInterval, Timeout.InfiniteTimeSpan);
            Common.Logger.LogInformation("Engram sweep timer active: every {N} minutes.", sweepIntervalMinutes);
        }
    }

    internal Task<int> PurgeNotes() => brain.PurgeAllNotes();

    public void Dispose()
    {
        sweepTimer?.Dispose();
        engramLock.Dispose();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private async Task SweepThreadsAsync()
    {
        try
        {
            foreach (string threadKey in dialogue.ThreadKeys)
            {
                DateTime lastRun     = lastEngramRun.TryGetValue(threadKey, out DateTime t) ? t : DateTime.MinValue;
                DateTime lastMessage = dialogue.GetThreadLastMessageAt(threadKey);
                if (lastMessage <= lastRun) continue;

                IReadOnlyList<ChatMessage> history = dialogue.GetThreadHistory(threadKey);
                if (history.Count > 1)
                    await RunEngramAsync(threadKey, history, "sweep timer");
            }
        }
        finally
        {
            sweepTimer?.Change(sweepInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunEngramAsync(string threadKey, IReadOnlyList<ChatMessage> history, string trigger)
    {
        if (!await engramLock.WaitAsync(0)) return;
        try
        {
            lastEngramRun[threadKey] = DateTime.UtcNow;

            List<string> existingNotes = await brain.GetNoteTitles();
            string existingNotesList   = existingNotes.Count > 0 ? string.Join(", ", existingNotes) : "none";
            string transcript          = BuildTranscript(history);
            string engramThreadKey     = $"engram:{Guid.NewGuid()}";

            if (context is not null)
                await context.RebuildFromTranscriptAsync(threadKey, transcript);

            string contextSummary = context?.GetContext(threadKey) ?? string.Empty;

            Common.Logger.LogInformation("[Engram] analysing thread [{ThreadKey}] (trigger: {Trigger})", threadKey, trigger);
            Common.Logger.LogInformation("[Engram] aware of {Count} existing note(s): {Notes}",
                existingNotes.Count,
                existingNotes.Count > 0 ? string.Join(", ", existingNotes) : "none");

            string contextBlock = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"CONTEXT SUMMARY (use this to resolve all pronouns and identify topics):\n{contextSummary}\n\n";

            // Steps 1–2: recursive fetch loop.
            // Each round Engram can request notes; if those notes reference others it wants to read,
            // it can request those too. Limited to fetchDepth rounds.
            HashSet<string> alreadyFetched = new(StringComparer.OrdinalIgnoreCase);

            for (int depth = 0; depth < fetchDepth; depth++)
            {
                string fetchPrompt = depth == 0
                    ? "Analyse this conversation and the list of existing notes.\n\n" +
                      contextBlock +
                      $"CONVERSATION:\n{transcript}\n\n" +
                      $"EXISTING NOTES: {existingNotesList}\n\n" +
                      "Identify any notes you want to read before extracting — to check for duplicates and to add to existing notes. " +
                      "Any note you intend to update must be fetched first.\n" +
                      "Respond ONLY with: {\"fetch\": [\"Name1\"]} — or {\"fetch\": []} to proceed straight to extraction."
                    : "If you need to read any further notes referenced in those you just received " +
                      $"(e.g. a [[link]] you saw), request them now. Already fetched: {string.Join(", ", alreadyFetched)}.\n" +
                      "Respond with {\"fetch\": [\"Name\"]} to request more, or {\"fetch\": []} to proceed to extraction.";

                string fetchRaw      = await PromptThread(engramThreadKey, fetchPrompt);
                List<string> toFetch = ParseFetchList(fetchRaw)
                    .Where(n => !alreadyFetched.Contains(n))
                    .ToList();

                if (toFetch.Count == 0)
                {
                    if (depth == 0) Common.Logger.LogInformation("[Engram] requested no notes.");
                    break;
                }

                Common.Logger.LogInformation("[Engram] fetch depth {Depth}: {Notes}",
                    depth + 1, string.Join(", ", toFetch));

                StringBuilder sb = new();
                foreach (string name in toFetch)
                {
                    string? noteContent = await brain.GetNoteForEngram(name);
                    if (noteContent is null) continue;
                    alreadyFetched.Add(name);
                    sb.AppendLine($"--- {name} ---");
                    sb.AppendLine(noteContent);
                    sb.AppendLine("---");
                }

                if (sb.Length > 0)
                    await PromptThread(engramThreadKey, $"Here are the notes you requested:\n\n{sb}");
            }

            // Step 3: extract
            string contextPreamble = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"Use the context summary to resolve all pronouns before extracting:\n{contextSummary}\n\n";

            string extractPrompt = contextPreamble + (ExtractionPrompt ?? string.Empty);
            string extractRaw    = await PromptThread(engramThreadKey, extractPrompt);

            (List<EngramAdd> adds, List<EngramEdit> edits) = ParseEngramOutput(extractRaw);

            int total = adds.Count + edits.Count;
            if (total > 0)
            {
                Common.Logger.LogInformation("[Engram] {Adds} add(s), {Edits} edit(s) from [{ThreadKey}].",
                    adds.Count, edits.Count, threadKey);
                if (adds.Count > 0) await brain.AddNotes(adds);
                if (edits.Count > 0) await brain.EditNotes(edits);
            }
            else
            {
                Common.Logger.LogInformation("[Engram] found nothing new in [{ThreadKey}].", threadKey);
            }
        }
        finally
        {
            engramLock.Release();
        }
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> history)
    {
        StringBuilder sb = new();
        foreach (ChatMessage msg in history)
        {
            if (msg.Role == "system") continue;
            string speaker = msg.Role == "user" ? "User" : "ARI";
            sb.AppendLine($"{speaker}: {msg.Content}");
        }
        return sb.ToString();
    }

    private static List<string> ParseFetchList(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("fetch", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch { }
        return new List<string>();
    }

    private static (List<EngramAdd> adds, List<EngramEdit> edits) ParseEngramOutput(string raw)
    {
        raw = Regex.Replace(raw, @"```[a-zA-Z]*\n?", "").Trim('`').Trim();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = new();
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? noteName = el.GetString("name");
                    string? content  = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        adds.Add(new EngramAdd { NoteName = noteName, Content = content });
                }
            }

            List<EngramEdit> edits = new();
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? noteName    = el.GetString("name");
                    string? newNoteName = el.GetString("newName");
                    string? content     = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        edits.Add(new EngramEdit { NoteName = noteName, NewNoteName = newNoteName, Content = content });
                }
            }

            return (adds, edits);
        }
        catch
        {
            return (new(), new());
        }
    }
}

file static class JsonElementExtensions
{
    internal static string? GetString(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
