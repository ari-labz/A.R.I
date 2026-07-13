using System.Text.Json;

namespace ARI.LLM;

/// <summary>read_file tool — thin wrapper that delegates to the thread's <see cref="FileSystem"/>.</summary>
internal sealed class ReadFile : Tool
{
    private readonly FileSystem fs;
    internal ReadFile(FileSystem fs) => this.fs = fs;

    internal override string Name => "read_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "read_file",
            description =
                "Read a SPECIFIC RANGE of a source file — use this sparingly. Most of the time preview_file is enough: it gives the exact " +
                "members to USE a type, so you do NOT need to read it. Only read when you must see how a specific method BEHAVES inside because " +
                "you are copying/imitating it — and then read just THAT method's lines (preview gave you its line number), not the whole file. " +
                "HARD LIMIT: at most 100 lines per call — wider requests are rejected without being read. ALWAYS preview_file first, then pass " +
                "start_line and end_line for the exact range. Reading a whole file, or reading 'to be sure', bloats your context and is the main " +
                "reason this pipeline runs out of room before it finishes. You never need to read a file you have already read.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path       = new { type = "string",  description = "Path to the file relative to the project root." },
                    start_line = new { type = "integer", description = "First line to return (1-indexed, inclusive). Use this whenever you know roughly where the content is." },
                    end_line   = new { type = "integer", description = "Last line to return (1-indexed, inclusive). Pair with start_line — read a window, not the whole file." }
                },
                required   = new[] { "path" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson) => fs.Read(argsJson);

    /// <summary>Hard per-call read window shared by every read_file backend (server disk and remote client).
    /// Every token a read returns is uncached prompt the next request must prefill (~60-70 t/s on the local
    /// server), so one whole-file read of a big class costs minutes of stall. Oversized reads are rejected
    /// before any bytes are read or forwarded; chained windows stack contiguously in the model's context
    /// into the same view a whole-file read would have given.</summary>
    internal const int WindowLines = 100;

    /// <summary>Parses a read_file range tolerantly. Missing start_line = 1; missing end_line = int.MaxValue
    /// ("to the end"). A read with neither is the whole file (1..MaxValue).</summary>
    internal static (int Start, int End) ExtractRange(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            int start = root.TryGetProperty("start_line", out JsonElement se) && TryGetLineArg(se, out int s) && s > 0 ? s : 1;
            int end   = root.TryGetProperty("end_line",   out JsonElement ee) && TryGetLineArg(ee, out int e) && e > 0 ? e : int.MaxValue;
            return (start, Math.Max(start, end));
        }
        catch { return (1, int.MaxValue); }
    }

    /// <summary>Returns a rejection message if the requested read exceeds <see cref="WindowLines"/>, else null.
    /// <paramref name="totalLines"/> is the file's line count when known (0 = unknown, e.g. a remote file that
    /// was never previewed). <paramref name="previewed"/> gates the unranged case: an un-previewed unranged
    /// read returns null so the caller's preview gate/divert can answer with the outline instead.</summary>
    internal static string? CheckWindow(string argsJson, string path, int totalLines, bool previewed)
    {
        (int start, int end) = ExtractRange(argsJson);

        if (start == 1 && end == int.MaxValue)   // unranged
        {
            if (totalLines > 0 && totalLines <= WindowLines) return null;   // whole small file — fine
            if (!previewed) return null;                                    // preview gate/divert answers instead
            string size = totalLines > 0 ? $"{path} has {totalLines} lines. " : "";
            return $"[Read window] read_file returns at most {WindowLines} lines per call — pick a range. {size}" +
                   $"Use the preview outline or search_files to target the right section, then read it with " +
                   $"start_line/end_line. To cover a longer stretch, read consecutive {WindowLines}-line windows " +
                   $"(e.g. 1-{WindowLines}, then {WindowLines + 1}-{WindowLines * 2}) — they stack in your context as one continuous view.";
        }

        int effectiveEnd = totalLines > 0 ? Math.Min(end, totalLines) : end;
        if (effectiveEnd - start + 1 <= WindowLines) return null;
        string reqEnd = end == int.MaxValue ? "the end" : end.ToString();
        return $"[Read window] read_file returns at most {WindowLines} lines per call — you asked for lines " +
               $"{start} to {reqEnd}. Read {start}-{start + WindowLines - 1} now, then continue with " +
               $"start_line={start + WindowLines} if you need the next section — consecutive windows stack " +
               $"in your context as one continuous view.";
    }

    // Models emit line numbers as quoted strings under the text protocol; cast tolerantly for the display label.
    private static bool TryGetLineArg(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString()?.Trim('"', '\'', ' '), out value);
        value = 0;
        return false;
    }

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc     = JsonDocument.Parse(args);
            string             relPath = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;
            string             safe    = Path.GetFileName(relPath).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

            string suffix = "";
            if (doc.RootElement.TryGetProperty("start_line", out JsonElement s) &&
                doc.RootElement.TryGetProperty("end_line",   out JsonElement e) &&
                TryGetLineArg(s, out int sl) && TryGetLineArg(e, out int el))
                suffix = $" ({sl}–{el})";

            return $"<!--ari-tool-start:read_file:{safe.Replace("--", "&#45;&#45;")}{suffix}-->";
        }
        catch { return "<!--ari-tool-start:read_file:file-->"; }
    };
}
