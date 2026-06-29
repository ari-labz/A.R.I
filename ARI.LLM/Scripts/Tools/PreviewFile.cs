using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// Returns a lightweight structural outline of a file — line count, size, and navigable landmarks
/// (class/method signatures for C#, top-level keys for JSON, headings for Markdown, etc.) with
/// their line numbers. The model uses this to orient itself before committing to a ranged read_file.
/// </summary>
internal sealed class PreviewFile : FileTool
{
    private const int PREVIEW_HEAD_LINES = 8;
    private const int MAX_OUTLINE_ITEMS  = 80;

    private readonly FileSnapshots? gate;
    internal PreviewFile(string root, CancellationToken ct, FileSnapshots? gate = null) : base(root, ct) { this.gate = gate; }

    internal override string Name => "preview_file";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "preview_file",
            description =
                "Get a structural outline of a file — line count, file size, and landmarks " +
                "(classes, methods, properties, JSON keys, Markdown headings, etc.) with their line numbers. " +
                "Call this BEFORE read_file on any file you haven't read yet. " +
                "Use the line numbers it returns to do a targeted read_file with start_line/end_line " +
                "rather than reading the whole file.",
            parameters = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to the file relative to the project root." }
                },
                required = new[] { "path" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        try
        {
            using JsonDocument doc     = JsonDocument.Parse(argsJson);
            string             relPath = (doc.RootElement.GetProperty("path").GetString() ?? "").Trim('"', '\'', ' ');
            string?            absPath = Resolve(relPath);

            if (absPath is null)       return "Access denied: path traversal is not allowed.";
            if (!File.Exists(absPath)) return $"File not found: {relPath}";
            gate?.MarkPreviewed(absPath);   // read_file on this file is now allowed

            string[] lines     = await File.ReadAllLinesAsync(absPath, ct);
            long     sizeBytes = new FileInfo(absPath).Length;
            string   ext       = Path.GetExtension(relPath).ToLowerInvariant();

            StringBuilder sb = new();
            sb.AppendLine($"[preview: \"{relPath}\" — {lines.Length} lines, {FormatSize(sizeBytes)}]");

            List<(int Line, string Label)> landmarks = ext switch
            {
                ".cs"   => ExtractCsharp(lines),
                ".json" => ExtractJson(lines),
                ".md" or ".markdown" => ExtractMarkdown(lines),
                ".ts" or ".tsx" or ".js" or ".jsx" => ExtractJs(lines),
                ".py"   => ExtractPython(lines),
                _       => new List<(int, string)>()
            };

            if (landmarks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Outline (line: symbol):");
                foreach ((int ln, string label) in landmarks.Take(MAX_OUTLINE_ITEMS))
                    sb.AppendLine($"  {ln,5}| {label}");
                if (landmarks.Count > MAX_OUTLINE_ITEMS)
                    sb.AppendLine($"  ... ({landmarks.Count - MAX_OUTLINE_ITEMS} more — use search_files to narrow)");
            }
            else
            {
                // No landmarks — show the first few lines as a plain preview
                sb.AppendLine();
                sb.AppendLine($"First {Math.Min(PREVIEW_HEAD_LINES, lines.Length)} lines:");
                for (int i = 0; i < Math.Min(PREVIEW_HEAD_LINES, lines.Length); i++)
                    sb.AppendLine($"  {i + 1,5}| {lines[i]}");
            }

            sb.AppendLine();
            sb.AppendLine($"[{lines.Length} lines total]");
            if (lines.Length > 400)
                sb.Append("Warning: this is a large file. Read ONLY the line ranges you need with read_file (start_line/end_line). Do not read the whole file.");
            else
                sb.Append("Use read_file with start_line/end_line to read a specific section.");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error previewing file: {ex.Message}";
        }
    }

    // ── Language extractors ───────────────────────────────────────────────────

    private static readonly Regex CsType   = new(@"^\s*(public|internal|private|protected|file)[\w\s<>\[\],?]*\s+(class|interface|record|struct|enum)\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex CsMember = new(@"^\s*(public|internal|private|protected|static|override|virtual|abstract|async)[\w\s<>\[\],?]*\s+(\w+)\s*[\({]", RegexOptions.Compiled);
    private static readonly Regex CsProp   = new(@"^\s*(public|internal|private|protected)[\w\s<>\[\],?]+\s+(\w+)\s*\{", RegexOptions.Compiled);

    private static List<(int, string)> ExtractCsharp(string[] lines)
    {
        List<(int, string)> out_ = new();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match m;
            if ((m = CsType.Match(line)).Success)
                out_.Add((i + 1, $"{m.Groups[2].Value} {m.Groups[3].Value}"));
            else if ((m = CsMember.Match(line)).Success && !line.TrimStart().StartsWith("//"))
                out_.Add((i + 1, m.Groups[2].Value + (line.Contains('(') ? "()" : "")));
        }
        return out_;
    }

    private static readonly Regex JsonKey = new(@"^\s*""([^""]+)""\s*:", RegexOptions.Compiled);

    private static List<(int, string)> ExtractJson(string[] lines)
    {
        List<(int, string)> out_ = new();
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i];
            foreach (char c in t) { if (c == '{' || c == '[') depth++; else if (c == '}' || c == ']') depth--; }
            // Only surface top-two levels to avoid drowning in nested JSON
            if (depth <= 2)
            {
                Match m = JsonKey.Match(t);
                if (m.Success) out_.Add((i + 1, m.Groups[1].Value));
            }
        }
        return out_;
    }

    private static readonly Regex MdHeading = new(@"^(#{1,4})\s+(.+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractMarkdown(string[] lines)
    {
        List<(int, string)> out_ = new();
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = MdHeading.Match(lines[i]);
            if (m.Success) out_.Add((i + 1, m.Groups[1].Value + " " + m.Groups[2].Value.Trim()));
        }
        return out_;
    }

    private static readonly Regex JsFunc  = new(@"^\s*(export\s+)?(default\s+)?(async\s+)?function\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex JsArrow = new(@"^\s*(export\s+)?(const|let)\s+(\w+)\s*=\s*(async\s+)?\(", RegexOptions.Compiled);
    private static readonly Regex JsClass = new(@"^\s*(export\s+)?(default\s+)?class\s+(\w+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractJs(string[] lines)
    {
        List<(int, string)> out_ = new();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match m;
            if      ((m = JsClass.Match(line)).Success) out_.Add((i + 1, $"class {m.Groups[3].Value}"));
            else if ((m = JsFunc.Match(line)).Success)  out_.Add((i + 1, $"{m.Groups[4].Value}()"));
            else if ((m = JsArrow.Match(line)).Success) out_.Add((i + 1, $"{m.Groups[3].Value}()"));
        }
        return out_;
    }

    private static readonly Regex PyDef   = new(@"^(\s*)(def|class)\s+(\w+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractPython(string[] lines)
    {
        List<(int, string)> out_ = new();
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = PyDef.Match(lines[i]);
            if (m.Success)
            {
                string indent = m.Groups[1].Value;
                string kind   = m.Groups[2].Value;
                string name   = m.Groups[3].Value;
                string prefix = indent.Length == 0 ? "" : "  ";
                out_.Add((i + 1, $"{prefix}{kind} {name}"));
            }
        }
        return out_;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024):F1} MB"
    };

    internal override Func<string, string>? Display => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            string name = Path.GetFileName(doc.RootElement.GetProperty("path").GetString() ?? "file")
                .Replace("&", "&amp;").Replace("<", "&lt;");
            return $"<div class=\"tool-use\">Previewing {name}</div>\n";
        }
        catch { return "<div class=\"tool-use\">Previewing file</div>\n"; }
    };
}
