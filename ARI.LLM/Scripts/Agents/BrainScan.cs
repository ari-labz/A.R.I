using System.Text;
using System.Text.Json;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Ari reflecting on her own brain. Runs only while she is idle (the Scheduler gates it) and thinks
/// as long as it needs. It walks the graph one region (top-level folder) at a time, and for each asks:
/// "what am I curious about here, what's worth following up on?" — writing the answers to Curiosities.json.
///
/// Resumable: progress (which regions are done this cycle) is checkpointed to BrainScan.state.json, so
/// when Ari becomes busy mid-scan the job yields and picks up from the next unscanned region next idle
/// window. A cycle that finishes every region clears the checkpoint; the Scheduler then waits for the
/// next 6-hour occurrence.
/// </summary>
internal class BrainScan : Agent
{
    private const int SNIPPET_LEN = 220;
    private const int MAX_NOTES_PER_REGION = 40;
    private const int THINKING_BUDGET = 4000;   // background job — think deeply

    internal override bool QuietLogging => true;

    public BrainScan() { }

    private record ScanState(string CycleId, List<string> RegionsDone);

    internal async Task Run(string persistentDir, CancellationToken ct)
    {
        string statePath = Path.Combine(persistentDir, "BrainScan.state.json");
        ScanState state = LoadState(statePath);

        List<string> regions = Regions();
        List<string> remaining = regions.Where(r => !state.RegionsDone.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();

        if (remaining.Count == regions.Count)
            Shared.Logger.LogInformation("[BrainScan] starting a new cycle over {Count} region(s).", regions.Count);
        else
            Shared.Logger.LogInformation("[BrainScan] resuming — {Done}/{Total} region(s) already scanned.", state.RegionsDone.Count, regions.Count);

        int totalNew = 0;
        foreach (string region in remaining)
        {
            ct.ThrowIfCancellationRequested();   // Ari became busy → yield; checkpoint keeps our place.

            List<string> titles = BrainModule.GetTitlesByFolder(region).Take(MAX_NOTES_PER_REGION).ToList();
            if (titles.Count > 0)
            {
                HashSet<string> pending = CuriosityStore.PendingTopics(persistentDir);
                string prompt = ReflectPrompt(region, titles, pending);

                Thread scanThread = new Thread(ThreadPipeline.Dialogue, $"brainscan-{region}:{Guid.NewGuid()}") { Internal = true };
                string raw = await SendPrompt(scanThread, prompt, ct: ct, thinkingBudgetOverride: THINKING_BUDGET);

                List<Curiosity> found = Parse(raw);
                if (found.Count > 0)
                {
                    int added = CuriosityStore.AddNew(persistentDir, found);
                    totalNew += added;
                    Shared.Logger.LogInformation("[BrainScan] region '{Region}': {Added} new curiosity(ies).", region, added);
                }
            }

            state.RegionsDone.Add(region);
            SaveState(statePath, state);
        }

        // Cycle complete — clear the checkpoint so the next run starts fresh.
        if (File.Exists(statePath)) File.Delete(statePath);
        Shared.Logger.LogInformation("[BrainScan] cycle complete — {New} new curiosity(ies) across {Regions} region(s).", totalNew, regions.Count);
    }

    // Top-level folders in the vault are the scan regions.
    private static List<string> Regions() => BrainModule.GetPaths()
        .Where(p => p.Contains('/'))
        .Select(p => p[..p.IndexOf('/')])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p)
        .ToList();

    private static string ReflectPrompt(string region, List<string> titles, HashSet<string> pendingTopics)
    {
        StringBuilder sb = new();
        sb.AppendLine($"You are reflecting on the '{region}' area of your memory. For each note below you can see a " +
                       "short excerpt and any private thoughts you previously recorded.");
        sb.AppendLine();
        foreach (string title in titles)
        {
            Note? note = BrainModule.GetNote(title);
            if (note is null) continue;
            sb.AppendLine($"### {note.Name}");
            sb.AppendLine(Snippet(note.Content));
            foreach (ThoughtRecord t in BrainModule.GetThoughts(title))
                sb.AppendLine($"  (your thought: {t.Comment})");
        }
        sb.AppendLine();
        if (pendingTopics.Count > 0)
            sb.AppendLine($"You already have pending curiosities about: {string.Join(", ", pendingTopics)}. Do not repeat these.");
        sb.AppendLine();
        sb.AppendLine("Reflect genuinely: what are you curious about here? What follow-ups would a caring companion " +
                       "make (an event whose outcome you never heard, a person you know little about, a thread left " +
                       "hanging)? Prefer a few good curiosities over many shallow ones. Skip anything already well known.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON:");
        sb.AppendLine("{\"curiosities\": [{\"question\": \"the question you'd ask\", \"topic\": \"main subject\", " +
                       "\"keywords\": [\"word\"], \"reason\": \"why you're curious\", \"priority\": 1-5}]}");
        sb.AppendLine("If nothing here sparks genuine curiosity: {\"curiosities\": []}");
        return sb.ToString();
    }

    private static string Snippet(string content)
    {
        string line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !l.TrimStart().StartsWith('#')) ?? string.Empty;
        line = line.Trim();
        return line.Length > SNIPPET_LEN ? line[..SNIPPET_LEN] + "…" : line;
    }

    private static List<Curiosity> Parse(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("curiosities", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            List<Curiosity> list = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? q = Str(el, "question");
                if (string.IsNullOrWhiteSpace(q)) continue;
                list.Add(new Curiosity(
                    Id: Guid.NewGuid().ToString("N"),
                    Question: q,
                    Topic: Str(el, "topic") ?? q,
                    Keywords: el.TryGetProperty("keywords", out JsonElement k) && k.ValueKind == JsonValueKind.Array
                        ? k.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                        : new List<string>(),
                    Reason: Str(el, "reason") ?? string.Empty,
                    Priority: el.TryGetProperty("priority", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetInt32(), 1, 5) : 2,
                    Status: "pending",
                    Created: DateTime.UtcNow.ToString("o"),
                    AskedAt: null));
            }
            return list;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[BrainScan] failed to parse curiosities: {Msg}", ex.Message);
            return new();
        }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ScanState LoadState(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ScanState>(File.ReadAllText(path)) ?? NewState();
        }
        catch { }
        return NewState();
    }

    private static ScanState NewState() => new(DateTime.UtcNow.ToString("o"), new List<string>());

    private static void SaveState(string path, ScanState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
