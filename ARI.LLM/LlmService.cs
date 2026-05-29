using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

public class LlmService : IDisposable
{
    private static readonly TimeSpan EngramInterval = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Model> models;
    private readonly List<string> ollamaModelnames;
    private readonly BrainService? brain;

    private readonly Dictionary<string, DateTime> lastEngramRun = new();
    private readonly Timer engramTimer;
    private readonly SemaphoreSlim engramLock = new(1, 1);

    public LlmService(string modelsConfigPath, string? brainConfigPath = null, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        AriModelsConfig config = AriModelsConfig.LoadFrom(modelsConfigPath);

        models = new Dictionary<string, Model>();
        ollamaModelnames = new List<string>();

        foreach (ModelConfig modelConfig in config.Models.Where(m => m.Enabled))
        {
            models[modelConfig.Name] = new Model(modelConfig);
            ollamaModelnames.Add(modelConfig.Model);
        }

        if (brainConfigPath is not null && models.ContainsKey("Engram"))
        {
            brain = new BrainService(brainConfigPath, loggerFactory);

            if (models.TryGetValue("Dialogue", out Model? dialogue))
                dialogue.ThreadBufferFull += (threadKey, history) => _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => RunEngramAsync(threadKey, history));

            Common.Logger.LogInformation("Engram is active. Brain connected.");
        }

        engramTimer = new Timer(_ => _ = SweepThreadsAsync(), null, EngramInterval, EngramInterval);
    }

    public IReadOnlyCollection<string> OllamaModelNames => ollamaModelnames.AsReadOnly();

    public Task<string> Prompt(string threadKey, string prompt, string? contextNote = null)
        => PromptModel("Dialogue", threadKey, prompt, contextNote);

    public Task<int> PurgeNotes() => brain?.PurgeAllNotes() ?? Task.FromResult(0);

    public void Dispose()
    {
        engramTimer.Dispose();
        engramLock.Dispose();
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private Task<string> PromptModel(string modelName, string threadKey, string prompt, string? contextNote = null)
    {
        if (!models.TryGetValue(modelName, out Model? model))
            throw new ModelNotFoundException($"Model '{modelName}' is not loaded or is not enabled.");

        return model.SendPrompt(threadKey, prompt, contextNote);
    }

    private async Task SweepThreadsAsync()
    {
        if (brain is null || !models.ContainsKey("Engram")) return;
        if (!models.TryGetValue("Dialogue", out Model? dialogue)) return;

        foreach (string threadKey in dialogue.ThreadKeys)
        {
            DateTime lastRun = lastEngramRun.TryGetValue(threadKey, out DateTime t) ? t : DateTime.MinValue;
            DateTime lastMessage = dialogue.GetThreadLastMessageAt(threadKey);

            if (lastMessage <= lastRun) continue;

            if (DateTime.UtcNow - lastRun >= EngramInterval)
            {
                IReadOnlyList<ChatMessage> history = dialogue.GetThreadHistory(threadKey);
                if (history.Count > 1)
                    await RunEngramAsync(threadKey, history);
            }
        }
    }

    private async Task RunEngramAsync(string threadKey, IReadOnlyList<ChatMessage> history)
    {
        if (brain is null) return;
        if (!await engramLock.WaitAsync(0)) return;
        try
        {
            lastEngramRun[threadKey] = DateTime.UtcNow;
            Common.Logger.LogInformation("Engram analysing thread [{ThreadKey}]...", threadKey);

            // TODO: optimise — currently fetches all note titles. Future: send search terms and fetch only relevant notes.
            List<string> existingNotes = await brain.GetNoteTitles();
            string existingNotesList = existingNotes.Count > 0 ? string.Join(", ", existingNotes) : "none";
            string transcript = BuildTranscript(history);
            string engramThreadKey = $"engram:{Guid.NewGuid()}";

            // Step 1: send transcript + note list, ask which notes Engram wants to read before extracting
            string fetchPrompt =
                $"Analyse this conversation transcript and the list of existing notes.\n\n" +
                $"CONVERSATION:\n{transcript}\n\n" +
                $"EXISTING NOTES: {existingNotesList}\n\n" +
                "Before extracting, identify any existing notes you want to read in full to confirm identity or avoid duplicates.\n" +
                "Respond ONLY with: {\"fetch\": [\"Name1\", \"Name2\"]} — or {\"fetch\": []} if none needed.";

            string fetchRaw = await PromptModel("Engram", engramThreadKey, fetchPrompt);
            List<string> toFetch = ParseFetchList(fetchRaw);

            // Step 2: fetch requested note contents and return them to Engram
            if (toFetch.Count > 0)
            {
                StringBuilder sb = new();
                foreach (string name in toFetch)
                {
                    string? content = await brain.GetNoteContent(name);
                    if (content is null) continue;
                    sb.AppendLine($"--- {name} ---");
                    sb.AppendLine(content);
                    sb.AppendLine("---");
                }

                if (sb.Length > 0)
                    await PromptModel("Engram", engramThreadKey, $"Here are the notes you requested:\n\n{sb}");
            }

            // Step 3: request final extraction
            string extractPrompt =
                "Now extract all factual details from the conversation.\n" +
                "Respond ONLY with a JSON array of note objects. Each object must have:\n" +
                "  category: \"People\" | \"Places\" | \"Events\" | \"Unknown\"\n" +
                "  name: string\n" +
                "  aliases: string[]\n" +
                "  pronouns: string | null\n" +
                "  relation: string | null\n" +
                "  events: string[]\n" +
                "  info: string[]\n" +
                "  observations: string[]  (Ari's inferences about this entity)\n" +
                "  feelings: string[]  (how [REDACT] feels about this entity)\n" +
                "  date: string | null  (DD/MM/YYYY, Events only)\n" +
                "  mergeWith: string | null  (EXACT name of an existing note this is the same as)\n\n" +
                "Rules:\n" +
                "- Notes with an identical name to an existing note will be merged into it automatically.\n" +
                "- Resolve all pronouns to specific named entities before extracting.\n" +
                "- Use ONLY names that appear verbatim in the conversation.\n" +
                "- INLINE LINKS: embed named entity references as {{LINK:EntityName}} in info/observation strings.\n" +
                "- If nothing useful was found, return: []\n" +
                "- Raw JSON only. No markdown, no explanation.";

            string extractRaw = await PromptModel("Engram", engramThreadKey, extractPrompt);
            Common.Logger.LogDebug("Engram raw response: {Raw}", extractRaw);

            List<ExtractedNote> notes = ParseEngramResponse(extractRaw);
            if (notes.Count > 0)
            {
                Common.Logger.LogInformation("Engram extracted {Count} note(s) from [{ThreadKey}].", notes.Count, threadKey);
                foreach (ExtractedNote note in notes)
                    await brain.SaveNote(note);
            }
            else
            {
                Common.Logger.LogInformation("Engram found nothing new in [{ThreadKey}].", threadKey);
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

    private static List<ExtractedNote> ParseEngramResponse(string raw)
    {
        raw = Regex.Replace(raw, @"```[a-z]*\n?", "").Trim('`').Trim();

        int arrayStart = raw.IndexOf('[');
        if (arrayStart > 0) raw = raw[arrayStart..];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            List<ExtractedNote> notes = new();

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                ExtractedNote note = new()
                {
                    Category     = ParseCategory(el.GetString("category")),
                    Name         = el.GetString("name") ?? string.Empty,
                    Pronouns     = el.GetString("pronouns"),
                    Relation     = el.GetString("relation"),
                    Date         = el.GetString("date"),
                    MergeWith    = el.GetString("mergeWith"),
                    Aliases      = el.GetStringList("aliases"),
                    Events       = el.GetStringList("events"),
                    Info         = el.GetStringList("info"),
                    Observations = el.GetStringList("observations"),
                    Feelings     = el.GetStringList("feelings")
                };

                if (!string.IsNullOrWhiteSpace(note.Name))
                    notes.Add(note);
            }

            return notes;
        }
        catch
        {
            return new List<ExtractedNote>();
        }
    }

    private static NoteCategory ParseCategory(string? value) => value?.ToLowerInvariant() switch
    {
        "people" => NoteCategory.People,
        "places" => NoteCategory.Places,
        "events" => NoteCategory.Events,
        _        => NoteCategory.Unknown
    };
}

file static class JsonElementExtensions
{
    internal static string? GetString(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static List<string> GetStringList(this JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }
}
