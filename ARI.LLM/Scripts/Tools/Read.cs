using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>read_file tool. The base is the default: for a plain-text file it delegates to the thread's
/// <see cref="FileSystem"/> (which windows the read and, on the server, gates it behind a preview). For a
/// recognised binary type it fetches raw bytes and hands them to a subclass <see cref="Decode"/> — so
/// adding a file type is one new subclass. See Documentation/Server/Read-Tool-Hierarchy.md.</summary>
internal class Read : Tool
{
    protected readonly FileSystem fs;
    internal Read(FileSystem fs) => this.fs = fs;

    // A decoded document can't be line-windowed by fs.Read, so PostRun caps it. Set above the text path's
    // own char cap so a plain-text read (already within it) never trips this net.
    private const int MAX_TEXT_CHARS  = 60000;
    private const int MAX_IMAGE_BYTES = 20 * 1024 * 1024;

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
                "reason this pipeline runs out of room before it finishes. You never need to read a file you have already read. " +
                "You can also read an IMAGE — pass an image file's path, or an http(s) URL to an image, and you will SEE it (no download needed). " +
                "A .ipynb notebook path returns its cells as text.",
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

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        string path = ExtractPath(argsJson);

        if (IsUrl(path)) return await ReadWeb(path);

        Read? decoder = For(path, fs);

        if (decoder is null)   // plain text — the existing windowed, preview-gated path (both backends)
        {
            string text = await fs.Read(argsJson);
            if (!text.StartsWith("[Error", StringComparison.OrdinalIgnoreCase))
                fs.MarkRead(argsJson);
            return text;
        }

        try
        {
            byte[] raw = await fs.ReadBytes(path);
            return decoder.Decode(raw, path);
        }
        catch (NotSupportedException)
        {
            return $"[Error: this project's filesystem can't read raw bytes, so .{Extension(path)} files can't be decoded here.]";
        }
        catch (Exception ex)
        {
            return $"[Error reading {path}: {ex.Message}]";
        }
    }

    private static readonly HttpClient Http = new();

    private static bool IsUrl(string path)
        => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a web resource. The bytes are fetched once into memory and never written to disk. The
    /// response Content-Type — not the URL extension, which many image URLs (e.g. GitHub attachments) lack —
    /// decides the decoder: an image is handed to ReadImage so the vision model can see it; anything else
    /// comes back as text. Nothing here persists, so viewing a web image costs no disk.</summary>
    private async Task<ToolResult> ReadWeb(string url)
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ARI");
            using HttpResponseMessage res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                return $"[Error: {(int)res.StatusCode} fetching {url}.]";

            string contentType = res.Content.Headers.ContentType?.MediaType ?? "";
            byte[] bytes       = await res.Content.ReadAsByteArrayAsync();

            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return ReadImage.Image(bytes, contentType);

            // Not an image — return the text. fetch_page is the better tool for a full page, but a plain
            // text/JSON resource read this way is still useful.
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) { return $"[Error fetching {url}: {ex.Message}]"; }
    }

    /// <summary>Turns raw bytes into a result. The base reads them as UTF-8 text — the default and the
    /// fallback any subclass can reach via <c>base.Decode</c> when its own decode fails. Subclasses override
    /// this and nothing else. <paramref name="path"/> is available for extension-based hints (e.g. mime).</summary>
    protected virtual ToolResult Decode(byte[] raw, string path) => Encoding.UTF8.GetString(raw);

    /// <summary>Shared read policy on the way back to the model: guard an oversized image, and cap a decoded
    /// document that fs.Read never got to window. Every read subclass inherits this one hook.</summary>
    internal override ToolResult PostRun(Thread thread, string argsJson, ToolResult result)
    {
        if (result.Kind == ToolResult.ContentKind.Image)
            return result.Bytes.Length > MAX_IMAGE_BYTES
                ? $"[Error: image is {result.Bytes.Length / (1024 * 1024)}MB — too large to hand to the vision model. Ask for a smaller or downscaled copy.]"
                : result;

        if (result.Text.Length > MAX_TEXT_CHARS)
            return ToolResult.AsText(result.Text[..MAX_TEXT_CHARS] + $"\n[Truncated at {MAX_TEXT_CHARS} chars — this file is large; read a narrower part.]");
        return result;
    }

    /// <summary>Selects the decoder for a path by extension; null means plain text (the base's fs.Read path).
    /// Magic-byte sniffing is a later refinement — extension is enough for the first cut.</summary>
    private static Read? For(string path, FileSystem fs) => Extension(path) switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" => new ReadImage(fs),
        "ipynb"                                              => new ReadNotebook(fs),
        _                                                    => null
    };

    protected static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static string ExtractPath(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            return (doc.RootElement.TryGetProperty("path", out JsonElement p) ? p.GetString() : null)?.Trim('"', '\'', ' ') ?? "";
        }
        catch { return ""; }
    }

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
