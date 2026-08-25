using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// Single source of truth for <c>preview_file</c> output. Builds a structural outline from raw file
/// content — for C# a class-diagram-style header (type + base/interfaces, fields &amp; properties with
/// type + access, method signatures with line numbers, enum members) rich enough that the model can bind
/// to a type WITHOUT a full read. Used by both the server-disk path (<see cref="ServerFileSystem"/>) and
/// the client-disk path (ARI.API ClientFileSystem forwards raw content here), so there is one extractor,
/// no JS/C# divergence.
/// </summary>
public static class PreviewFormatter
{
    private const int PREVIEW_HEAD_LINES = 8;
    private const int MAX_OUTLINE_ITEMS  = 150;   // raised from 80: signatures produce more items than bare names
    private const int MAX_PARAM_CHARS    = 80;    // truncate long parameter lists in a method signature

    /// <summary>
    /// Build the full <c>[preview: …]</c> block for a file given its raw lines and byte size.
    /// The output starts with the <c>[preview:</c> prefix the client read-gate validates on.
    /// </summary>
    public static string Build(string relPath, IReadOnlyList<string> lines, long sizeBytes)
    {
        string ext = Path.GetExtension(relPath).ToLowerInvariant();

        StringBuilder sb = new();
        sb.AppendLine($"[preview: \"{relPath}\" — {lines.Count} lines, {FormatSize(sizeBytes)}]");

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
            sb.AppendLine();
            sb.AppendLine($"First {Math.Min(PREVIEW_HEAD_LINES, lines.Count)} lines:");
            for (int i = 0; i < Math.Min(PREVIEW_HEAD_LINES, lines.Count); i++)
                sb.AppendLine($"  {i + 1,5}| {lines[i]}");
        }

        sb.AppendLine();
        sb.AppendLine($"[{lines.Count} lines total]");
        if (ext == ".cs" && landmarks.Count > 0)
            sb.Append("This class-diagram outline lists member types and signatures — for a data class it is usually " +
                      "enough to bind to (field/property/method names + types are exact). read_file only when you need a body.");
        else if (lines.Count > 400)
            sb.Append("Warning: this is a large file. Read ONLY the line ranges you need with read_file (start_line/end_line). Do not read the whole file.");
        else
            sb.Append("Use read_file with start_line/end_line to read a specific section.");
        return sb.ToString();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024):F1} MB"
    };

    // ── C# class-diagram extractor ────────────────────────────────────────────────────────────────
    // type decl, capturing kind, name and the base/interface list after ':'
    private static readonly Regex CsType = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|file|sealed|abstract|static|partial|readonly|new)\s+)*\b(class|interface|record|struct|enum)\s+(\w+)(?:\s*<[^>]*>)?(?:\s*:\s*([^{]+?))?\s*(?:\{|=>|where\b|$)",
        RegexOptions.Compiled);
    // a member line must begin with an access/member modifier — cheaply excludes locals inside method bodies
    private static readonly Regex CsModPrefix = new(
        @"^\s*(?:\[[^\]]*\]\s*)*((?:public|private|protected|internal|static|readonly|const|virtual|override|abstract|async|sealed|new|extern|unsafe|volatile|required|partial|event|file)\s+)+",
        RegexOptions.Compiled);
    private static readonly Regex CsMethod   = new(@"^([\w<>\[\],\?\.]+\s+)?(\w+)\s*(?:<[^>]*>)?\s*\((.*?)\)", RegexOptions.Compiled);
    private static readonly Regex CsProperty = new(@"^([\w<>\[\],\?\.]+)\s+(\w+)\s*(?:\{|=>)", RegexOptions.Compiled);
    private static readonly Regex CsField    = new(@"^([\w<>\[\],\?\.]+)\s+(\w+)\s*(?:=|;|,|$)", RegexOptions.Compiled);
    private static readonly Regex CsEnumMember = new(@"^\s*(\w+)\s*(?:=[^,]+)?,?\s*(?://.*)?$", RegexOptions.Compiled);

    private static List<(int, string)> ExtractCsharp(IReadOnlyList<string> lines)
    {
        List<(int, string)> outp = new();
        int  enumDepth = -1;    // brace depth at which the current enum body sits; -1 == not in an enum
        int  depth     = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string raw     = lines[i];
            string trimmed = raw.TrimStart();

            // enum-member capture: emit bare members while inside an enum body
            if (enumDepth >= 0 && depth > enumDepth && trimmed.Length > 0
                && !trimmed.StartsWith("//") && !trimmed.StartsWith("[") && !trimmed.StartsWith("{"))
            {
                Match em = CsEnumMember.Match(trimmed);
                if (em.Success) outp.Add((i + 1, $"· {em.Groups[1].Value}"));
            }

            Match t = CsType.Match(raw);
            if (t.Success)
            {
                string kind = t.Groups[1].Value;
                string name = t.Groups[2].Value;
                string bases = t.Groups[3].Success ? t.Groups[3].Value.Trim() : "";
                outp.Add((i + 1, bases.Length > 0 ? $"{kind} {name} : {bases}" : $"{kind} {name}"));
                if (kind == "enum") enumDepth = depth;   // its body opens at the next '{'
            }
            else if (enumDepth < 0 || depth <= enumDepth)   // don't treat enum members as fields/methods
            {
                Match mod = CsModPrefix.Match(raw);
                if (mod.Success && !trimmed.StartsWith("//"))
                {
                    string rest = raw.Substring(mod.Index + mod.Length).TrimStart();
                    string sym  = AccessSymbol(mod.Groups[1].Captures);
                    Match  mm;
                    if ((mm = CsMethod.Match(rest)).Success && rest.Contains('('))
                    {
                        string ret    = mm.Groups[1].Success ? mm.Groups[1].Value.Trim() : "";
                        string mname  = mm.Groups[2].Value;
                        string prms   = Compact(mm.Groups[3].Value);
                        outp.Add((i + 1, ret.Length > 0 ? $"{sym} {ret} {mname}({prms})" : $"{sym} {mname}({prms})"));
                    }
                    else if ((mm = CsProperty.Match(rest)).Success && !rest.Contains('('))
                        outp.Add((i + 1, $"{sym} {mm.Groups[1].Value} {mm.Groups[2].Value}"));
                    else if ((mm = CsField.Match(rest)).Success)
                        outp.Add((i + 1, $"{sym} {mm.Groups[1].Value} {mm.Groups[2].Value}"));
                }
            }

            // update brace depth AFTER classifying this line
            foreach (char c in raw) { if (c == '{') depth++; else if (c == '}') { depth--; if (enumDepth >= 0 && depth <= enumDepth) enumDepth = -1; } }
        }
        return outp;
    }

    private static string AccessSymbol(CaptureCollection mods)
    {
        string blob = string.Join(' ', mods.Select(c => c.Value));
        if (blob.Contains("public"))    return "+";
        if (blob.Contains("protected")) return "#";
        if (blob.Contains("internal"))  return "~";
        return "-";   // explicit private, or a member with only static/readonly/const (C# default is private)
    }

    private static string Compact(string prms)
    {
        string p = Regex.Replace(prms.Trim(), @"\s+", " ");
        return p.Length > MAX_PARAM_CHARS ? p.Substring(0, MAX_PARAM_CHARS - 1) + "…" : p;
    }

    // ── other languages (unchanged behaviour, moved here so there is one preview builder) ──────────
    private static readonly Regex JsonKey = new(@"^\s*""([^""]+)""\s*:", RegexOptions.Compiled);

    private static List<(int, string)> ExtractJson(IReadOnlyList<string> lines)
    {
        List<(int, string)> outp = new();
        int depth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i];
            foreach (char c in t) { if (c == '{' || c == '[') depth++; else if (c == '}' || c == ']') depth--; }
            if (depth <= 2)
            {
                Match m = JsonKey.Match(t);
                if (m.Success) outp.Add((i + 1, m.Groups[1].Value));
            }
        }
        return outp;
    }

    private static readonly Regex MdHeading = new(@"^(#{1,4})\s+(.+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractMarkdown(IReadOnlyList<string> lines)
    {
        List<(int, string)> outp = new();
        for (int i = 0; i < lines.Count; i++)
        {
            Match m = MdHeading.Match(lines[i]);
            if (m.Success) outp.Add((i + 1, m.Groups[1].Value + " " + m.Groups[2].Value.Trim()));
        }
        return outp;
    }

    private static readonly Regex JsFunc  = new(@"^\s*(export\s+)?(default\s+)?(async\s+)?function\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex JsArrow = new(@"^\s*(export\s+)?(const|let)\s+(\w+)\s*=\s*(async\s+)?\(", RegexOptions.Compiled);
    private static readonly Regex JsClass = new(@"^\s*(export\s+)?(default\s+)?class\s+(\w+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractJs(IReadOnlyList<string> lines)
    {
        List<(int, string)> outp = new();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            Match m;
            if      ((m = JsClass.Match(line)).Success) outp.Add((i + 1, $"class {m.Groups[3].Value}"));
            else if ((m = JsFunc.Match(line)).Success)  outp.Add((i + 1, $"{m.Groups[4].Value}()"));
            else if ((m = JsArrow.Match(line)).Success) outp.Add((i + 1, $"{m.Groups[3].Value}()"));
        }
        return outp;
    }

    private static readonly Regex PyDef = new(@"^(\s*)(def|class)\s+(\w+)", RegexOptions.Compiled);

    private static List<(int, string)> ExtractPython(IReadOnlyList<string> lines)
    {
        List<(int, string)> outp = new();
        for (int i = 0; i < lines.Count; i++)
        {
            Match m = PyDef.Match(lines[i]);
            if (m.Success) outp.Add((i + 1, $"{m.Groups[2].Value} {m.Groups[3].Value}"));
        }
        return outp;
    }
}
