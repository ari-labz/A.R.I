using System.Text.Json;

namespace ARI.LLM;

/// <summary>edit_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class EditFile : Tool
{
    private readonly FileSystem fs;
    internal EditFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "edit_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_file",
            description = "Edit a file by line number. Two distinct operations — pick the right one:\n• REPLACE existing lines: set start_line and end_line (1-based inclusive) and new_string. For one line, start_line == end_line. An empty new_string deletes the range.\n• INSERT brand-new lines (a new method, field, using, etc.) WITHOUT changing any existing line: set insert_after to the line the new code should follow, and new_string to the new line(s). The text is added on its own line(s) right after that line; NOTHING is replaced (insert_after:0 inserts at the very top of the file). This is the correct way to add code between existing blocks — do NOT 'replace' an adjacent line (like a closing brace or a blank line) just to squeeze new code in: that drops the line you replaced and is the #1 cause of broken edits.\nMake the SMALLEST edit that does the job: change ONLY the lines that actually differ, and never re-type surrounding code that isn't changing (that is how lines get dropped or duplicated). To rename a symbol on lines 8, 10 and 13, edit those three lines individually. REQUIREMENT: call read_file first; you edit by the 1-based line numbers it shows. To do several changes at once, pass an 'edits' array of items — each item is either a replace ({start_line,end_line,new_string}) or an insert ({insert_after,new_string}); they resolve against the file as you last read it and apply together, so line numbers don't shift between them. Use write_file only for a brand-new file or a genuine whole-file rewrite — never to change a few lines.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path         = new { type = "string",  description = "File path relative to project root." },
                    start_line   = new { type = "integer", description = "REPLACE mode: first line to replace (1-based inclusive, exactly as shown by read_file/search_files). Omit when inserting." },
                    end_line     = new { type = "integer", description = "REPLACE mode: last line to replace (1-based inclusive). Equals start_line for a single-line change — the common case. Omit when inserting." },
                    insert_after = new { type = "integer", description = "INSERT mode: add new_string on its own line(s) immediately AFTER this 1-based line, replacing nothing (0 = insert at the top of the file). Use this to add new code between existing lines instead of replacing an anchor line." },
                    new_string   = new { type = "string",  description = "The new/replacement line(s) only — no read_file 'N|' prefix, no unchanged surrounding lines. In REPLACE mode it replaces start_line..end_line (empty deletes the range); in INSERT mode it is the text inserted after insert_after." },
                    edits        = new { type = "array",   description = "Several changes at once; each item is a replace {start_line,end_line,new_string} OR an insert {insert_after,new_string}, kept as narrow as possible. They resolve against the file as you last read it and apply together. Prefer many small items over one wide range." }
                },
                required = new[] { "path" }
            }
        }
    };

    internal override string? PreCheck(Thread thread, string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("path", out JsonElement p) && p.GetString() is { } path)
            {
                if (!fs.ReadLedger.Contains(path))
                    return $"[Blocked] You must call read_file or preview_file on '{path}' before editing it. Read it first so you have the current line numbers.";
            }
        }
        catch { }
        return null;
    }

    internal override Task<string> Execute(string argsJson) => fs.Edit(argsJson);

    // Emit the enriched tool-start marker (with the +A/-R diff computed from args), not a plain label — so the
    // committed card keeps its diff badges and the client flips it Editing→Edited via the trailing batch-end.
    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            JsonElement root = doc.RootElement;
            string file = Path.GetFileName((root.GetProperty("path").GetString() ?? "").Trim()).Replace("--", "&#45;&#45;");
            (int added, int removed) = DiffCounts.Of(root);
            string diff = (added > 0 ? $"|+{added}" : "") + (removed > 0 ? $"|-{removed}" : "");
            return $"<!--ari-tool-start:edit_file:{file}{diff}-->";
        }
        catch { return "<!--ari-tool-start:edit_file:file-->"; }
    };
}

/// <summary>Computes +added / -removed line counts for an edit/write call from its arguments, for the diff badge
/// on the tool card. Shared by edit_file and write_file Display.</summary>
internal static class DiffCounts
{
    internal static (int Added, int Removed) Of(JsonElement root, string contentProp = "new_string")
    {
        int added = 0, removed = 0;
        void One(JsonElement e)
        {
            if (e.TryGetProperty(contentProp, out JsonElement ns) && ns.ValueKind == JsonValueKind.String)
            {
                string s = ns.GetString() ?? "";
                if (s.Length > 0) added += s.Split('\n').Length;
            }
            int sl = Line(e, "start_line"), el = Line(e, "end_line");
            if (sl > 0 && el >= sl) removed += el - sl + 1;
        }
        if (root.TryGetProperty("edits", out JsonElement edits) && edits.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in edits.EnumerateArray()) One(e);
        else One(root);
        return (added, removed);
    }

    private static int Line(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m)) return m;
        return 0;
    }
}
