using System.Text.Json;

namespace ARI.LLM;

/// <summary>write_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class WriteFile : Tool
{
    private readonly FileSystem fs;
    internal WriteFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "write_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "write_file",
            description = "Create a NEW file, or deliberately replace an entire existing file's contents. Overwrites the whole file and creates missing parent directories. Do NOT use write_file to change an existing file — adding a method, editing lines, or fixing call sites is always edit_file. In particular, if edit_file feels stuck (line numbers shifted, an edit didn't seem to land), the fix is to re-read the file for fresh line numbers and use search_files to find exact call sites — NOT to fall back to write_file. Rewriting a whole existing file from memory reliably drops or duplicates code and is never the right escape hatch.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path    = new { type = "string", description = "File path relative to project root" },
                    content = new { type = "string", description = "The full content to write to the file" }
                },
                required = new[] { "path", "content" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Write(argsJson);

    // Enriched tool-start marker (with the +added diff from args) so the card keeps its badge and flips Writing→Wrote.
    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            JsonElement root = doc.RootElement;
            string file = Path.GetFileName((root.GetProperty("path").GetString() ?? "").Trim()).Replace("--", "&#45;&#45;");
            (int added, _) = DiffCounts.Of(root, contentProp: "content");
            string diff = added > 0 ? $"|+{added}" : "";
            return $"<!--ari-tool-start:write_file:{file}{diff}-->";
        }
        catch { return "<!--ari-tool-start:write_file:file-->"; }
    };
}
