using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.LLM;

/// <summary>
/// Turns a finished turn's trace into the tool calls it actually made, for replay into later turns' context.
/// Without this, a past turn re-entered context as prose only ("Checking… — No commits yet"), so the model
/// saw itself stating results with no call in between, and copied that: narrating tool use and inventing
/// the output instead of calling anything. Replaying the real calls keeps "did I check?" a fact in context.
/// Results are shrunk to a stub — the model re-runs a tool when it needs the output again.
/// </summary>
internal static class ToolHistory
{
    private const int RESULT_FIRST_LINE_CHARS = 160;
    private const int ARG_VALUE_CHARS         = 160;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The trace's tool calls, batched as they ran (calls issued together, before their results). <paramref name="keepResults"/>
    /// replays each call exactly as it was sent, result and all, for a thread whose reading is the point of keeping it.</summary>
    internal static List<IReadOnlyList<HistoryToolCall>> Batches(IEnumerable<TraceStep> trace, bool keepResults = false)
    {
        List<IReadOnlyList<HistoryToolCall>> batches = new();
        List<(string Name, string Args, string? Result)> current = new();
        bool resultsSeen = false;

        void Flush()
        {
            if (current.Count > 0)
                batches.Add(current.Select(c => keepResults
                    ? new HistoryToolCall(c.Name, c.Args, c.Result ?? "")
                    : new HistoryToolCall(c.Name, ShrinkArgs(c.Args), Stub(c.Result ?? ""))).ToList());
            current.Clear();
        }

        foreach (TraceStep step in trace)
        {
            if (step.Kind == "tool_call" && step.Name is { } name)
            {
                if (resultsSeen) { Flush(); resultsSeen = false; }
                current.Add((name, step.Args ?? "{}", null));
            }
            else if (step.Kind == "tool_result")
            {
                int i = current.FindIndex(c => c.Result is null && c.Name == step.Name);
                if (i >= 0) current[i] = current[i] with { Result = step.Text ?? "" };
                resultsSeen = true;
            }
        }
        Flush();
        return batches;
    }

    /// <summary>First line of a result, plus how much was left out — so it's clear the rest wasn't kept.</summary>
    internal static string Stub(string result)
    {
        string trimmed = result.Trim();
        string[] lines = trimmed.Split('\n');
        string first   = lines[0].Trim();
        if (lines.Length == 1 && first.Length <= RESULT_FIRST_LINE_CHARS) return first;
        if (first.Length > RESULT_FIRST_LINE_CHARS) first = first[..RESULT_FIRST_LINE_CHARS] + "…";
        return $"{first} … [earlier output, {lines.Length} lines — not kept; re-run the tool if you need it]";
    }

    /// <summary>Args with long string values (a whole file for write_file, a long message) cut to a preview. A call the model wrote with the
    /// same key twice ({"path":"a","path":"b"}) keeps the last value, as the tool did; it used to throw here on every later message and leave
    /// the whole thread unable to answer. Anything unreadable is trimmed as plain text rather than failing.</summary>
    internal static string ShrinkArgs(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);   // tolerates repeated keys, unlike JsonNode
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return argsJson;
            JsonObject obj = new();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                obj[p.Name] = p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > ARG_VALUE_CHARS } s
                    ? $"{s[..ARG_VALUE_CHARS]}… ({s.Length} chars)"
                    : JsonNode.Parse(p.Value.GetRawText());
            return obj.ToJsonString(Relaxed);
        }
        catch (Exception) { return argsJson.Length > ARG_VALUE_CHARS ? argsJson[..ARG_VALUE_CHARS] + "…" : argsJson; }
    }
}
