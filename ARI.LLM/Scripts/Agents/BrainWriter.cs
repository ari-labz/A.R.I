using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// Owns everything both Engram and Refactor used to duplicate: parsing an LLM's add/edit/delete/
// merge/thoughts JSON, and applying a parsed batch to the brain. Neither agent writes to
// BrainModule directly anymore — they build a ParsedBatch and hand it here.
internal static class BrainWriter
{
    internal record ParsedBatch(
        List<EngramAdd> Adds,
        List<EngramEdit> Edits,
        List<EngramDelete> Deletes,
        List<EngramMerge> Merges,
        List<EngramThought> Thoughts);

    internal record ApplyResult(int Succeeded, int Failed, List<NoteChange> Changes);

    // ── Apply ────────────────────────────────────────────────────────────────────────

    // summaryByTitle: bare title -> the 1-2 sentence plan summary, used only to label the UI event.
    internal static ApplyResult Apply(ParsedBatch batch, IReadOnlyDictionary<string, string> summaryByTitle)
    {
        List<NoteChange> changes = new();
        int failed = 0;

        if (batch.Adds.Count > 0) BrainModule.AddNotes(batch.Adds);
        if (batch.Edits.Count > 0) BrainModule.EditNotes(batch.Edits);

        static string BareName(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        foreach (EngramAdd add in batch.Adds)
        {
            string title = BareName(add.NoteName);
            Note? note = BrainModule.GetNote(title);
            if (note is null) { failed++; continue; }
            changes.Add(new NoteChange(title, note.Url, "created", summaryByTitle.GetValueOrDefault(title, "created")));
        }
        foreach (EngramEdit edit in batch.Edits)
        {
            string title = BareName(edit.NewNoteName ?? edit.NoteName);
            Note? note = BrainModule.GetNote(title);
            if (note is null) { failed++; continue; }
            changes.Add(new NoteChange(title, note.Url, "updated", summaryByTitle.GetValueOrDefault(title, "updated")));
        }

        foreach (EngramMerge merge in batch.Merges)
        {
            try { BrainModule.MergeNotes(merge.From, merge.Into); }
            catch (Exception ex) { Shared.Logger.LogWarning("[BrainWriter] Merge '{From}' -> '{Into}' failed: {Message}", merge.From, merge.Into, ex.Message); failed++; }
        }
        foreach (EngramDelete del in batch.Deletes)
        {
            try { BrainModule.DeleteNote(del.NoteName); }
            catch (Exception ex) { Shared.Logger.LogWarning("[BrainWriter] Delete '{Name}' failed: {Message}", del.NoteName, ex.Message); failed++; }
        }
        foreach (EngramThought thought in batch.Thoughts)
        {
            Note? note = BrainModule.GetNote(thought.NoteName);
            if (note is null) { Shared.Logger.LogWarning("[BrainWriter] Thought for unknown note '{Name}' dropped.", thought.NoteName); continue; }
            note.AddThought(thought.SpanText, thought.Comment, thought.Confidence, thought.Kind);
        }

        IEnumerable<string> writtenTitles = batch.Adds.Select(a => BareName(a.NoteName))
            .Concat(batch.Edits.Select(e => BareName(e.NewNoteName ?? e.NoteName)));
        BrainModule.MarkDirty(writtenTitles);

        return new ApplyResult(changes.Count, failed, changes);
    }

    // ── Parse ────────────────────────────────────────────────────────────────────────

    // Titles are globally unique, so a path-form link [[Folder/Sub/Title]] always means [[Title]].
    // Models emit the path form often; normalise it deterministically so links resolve and stay tidy,
    // rather than relying on the prompt to get it right every time. Display alias ([[x|disp]]) is kept.
    private static readonly Regex pathLink = new(@"\[\[[^\]|]*/([^\]|/]+)(\|[^\]]*)?\]\]", RegexOptions.Compiled);
    internal static string NormalizeLinks(string content) => pathLink.Replace(content, m => $"[[{m.Groups[1].Value}{m.Groups[2].Value}]]");

    internal static ParsedBatch Parse(string raw)
    {
        raw = StripFences(raw);
        int start = raw.IndexOf('{');
        if (start < 0) return new ParsedBatch([], [], [], [], []);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw[start..]);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = [];
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? name = el.GetStr("name"), content = el.GetStr("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        adds.Add(new EngramAdd { NoteName = name, Content = NormalizeLinks(content), Aliases = ParseAliases(el), Type = el.GetStr("type") });
                }

            List<EngramEdit> edits = [];
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? name = el.GetStr("name"), newName = el.GetStr("newName"), content = el.GetStr("content");
                    if (!string.IsNullOrWhiteSpace(name) && content is not null)
                        edits.Add(new EngramEdit { NoteName = name, NewNoteName = newName, Content = NormalizeLinks(content), Aliases = ParseAliases(el), Type = el.GetStr("type") });
                }

            List<EngramDelete> deletes = [];
            if (root.TryGetProperty("delete", out JsonElement delArr) && delArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in delArr.EnumerateArray())
                {
                    string? name = el.GetStr("name");
                    if (!string.IsNullOrWhiteSpace(name))
                        deletes.Add(new EngramDelete { NoteName = name, Reason = el.GetStr("reason") ?? string.Empty });
                }

            List<EngramMerge> merges = [];
            if (root.TryGetProperty("merge", out JsonElement mergeArr) && mergeArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in mergeArr.EnumerateArray())
                {
                    string? from = el.GetStr("from"), into = el.GetStr("into");
                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(into))
                        merges.Add(new EngramMerge { From = from, Into = into, Reason = el.GetStr("reason") ?? string.Empty });
                }

            List<EngramThought> thoughts = [];
            if (root.TryGetProperty("thoughts", out JsonElement thoughtArr) && thoughtArr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in thoughtArr.EnumerateArray())
                {
                    string? note = el.GetStr("note"), span = el.GetStr("spanText"), comment = el.GetStr("comment");
                    if (!string.IsNullOrWhiteSpace(note) && !string.IsNullOrWhiteSpace(span) && !string.IsNullOrWhiteSpace(comment))
                        thoughts.Add(new EngramThought
                        {
                            NoteName   = note,
                            SpanText   = span,
                            Comment    = comment,
                            Confidence = el.GetStr("confidence") ?? "unknown",
                            Kind       = el.GetStr("kind") ?? "observation"
                        });
                }

            return new ParsedBatch(adds, edits, deletes, merges, thoughts);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[BrainWriter] Failed to parse LLM output: {Error}. Raw (first 200): {Raw}",
                ex.Message, raw.Length > 200 ? raw[..200] : raw);
            return new ParsedBatch([], [], [], [], []);
        }
    }

    internal static IReadOnlyList<string> ParseAliases(JsonElement el)
    {
        if (!el.TryGetProperty("aliases", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    internal static string StripFences(string raw) => Regex.Replace(raw, @"```[a-zA-Z]*\n?", string.Empty).Trim('`').Trim();
}

file static class BrainWriterJson
{
    internal static string? GetStr(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
