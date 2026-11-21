using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>edit_files: one change across several files that lands whole or not at all. Each file's edits are the same as edit_file's; if any
/// file's edits fail, every file already changed in this call is put back as it was, so the project is never left half-changed.</summary>
internal sealed class EditFiles : Tool
{
    private readonly FileSystem fs;
    internal EditFiles(FileSystem fs) => this.fs = fs;

    internal override string Name => "edit_files";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_files",
            description = "Make one change across several files at once, all or nothing: if any file's edits fail, every file is put back as it was. " +
                          "Use it for a change that only works when every part lands, like renaming something used in several files. Each item is a path plus edits " +
                          "exactly like edit_file's edits array (replace {start_line,end_line,new_string} or insert {insert_after,new_string}), against the file as you last read it. Read each file first.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    files = new
                    {
                        type  = "array",
                        description = "One item per file: {path, edits}.",
                        items = new { type = "object", properties = new { path = new { type = "string" }, edits = new { type = "array" } }, required = new[] { "path", "edits" } },
                    },
                },
                required = new[] { "files" }
            }
        }
    };

    private static List<(string Path, string EditArgs)> Parse(string argsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(argsJson);
        List<(string, string)> files = new();
        if (!doc.RootElement.TryGetProperty("files", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return files;
        foreach (JsonElement f in arr.EnumerateArray())
            if (f.TryGetProperty("path", out JsonElement p) && p.GetString() is { Length: > 0 } path && f.TryGetProperty("edits", out JsonElement e))
                files.Add((path, JsonSerializer.Serialize(new { path, edits = e })));
        return files;
    }

    internal override string? PreCheck(Thread thread, string argsJson)
    {
        try
        {
            List<(string Path, string)> files = Parse(argsJson);
            if (files.Count == 0) return "[Blocked] Give files: an array of {path, edits}.";
            if (files.Select(f => PathScope.Norm(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) return "[Blocked] Each file appears once; put all of a file's edits in its one item.";
            string[] unread = files.Where(f => !fs.WasRead(f.Path)).Select(f => f.Path).ToArray();
            if (unread.Length > 0) return $"[Blocked] Read these first so you have their current line numbers: {string.Join(", ", unread)}.";
        }
        catch (JsonException) { return "[Blocked] The arguments aren't valid JSON."; }
        return null;
    }

    private readonly ConcurrentDictionary<string, string> windows = new();

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        List<(string Path, string EditArgs)> files = Parse(argsJson);
        Dictionary<string, string> before = new();
        foreach ((string path, _) in files)
        {
            try { before[path] = Encoding.UTF8.GetString(await fs.ReadBytes(path)); }
            catch (Exception e) { return (ToolResult)$"Error: couldn't read {path}: {e.Message}. Nothing was changed."; }
        }

        List<string> done = new();
        foreach ((string path, string editArgs) in files)
        {
            string result = await fs.Edit(editArgs);
            if (result.StartsWith("Successfully", StringComparison.Ordinal)) { done.Add(path); continue; }

            List<string> notRestored = new();
            foreach (string changed in done)
            {
                string put = await fs.Write(JsonSerializer.Serialize(new { path = changed, content = before[changed] }));
                if (!put.StartsWith("Successfully", StringComparison.Ordinal)) notRestored.Add(changed);
            }
            return (ToolResult)($"Error: the edits to {path} failed, so nothing was changed: {result}" +
                                (done.Count > 0 ? $" ({string.Join(", ", done)} {(done.Count == 1 ? "was" : "were")} put back as {(done.Count == 1 ? "it was" : "they were")}.)" : "") +
                                (notRestored.Count > 0 ? $" WARNING: couldn't put back {string.Join(", ", notRestored)}; check them." : ""));
        }

        StringBuilder markers = new();
        foreach ((string path, _) in files)
            try { markers.Append(EditWindow.Marker(path, before[path], Encoding.UTF8.GetString(await fs.ReadBytes(path)))); } catch { }
        windows[argsJson] = markers.ToString();
        return (ToolResult)$"Successfully edited {files.Count} files together: {string.Join(", ", files.Select(f => f.Path))}.";
    }

    internal override Func<string, string>? DisplayAfter => args => windows.TryRemove(args, out string? m) ? m : "";

    internal override Func<string, string>? Display => args =>
    {
        try { return $"<!--ari-tool-start:edit_files:{Parse(args).Count} files-->"; }
        catch { return "<!--ari-tool-start:edit_files:files-->"; }
    };
}
