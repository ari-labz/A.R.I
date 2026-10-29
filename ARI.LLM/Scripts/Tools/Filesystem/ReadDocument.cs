using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace ARI.LLM;

/// <summary>read_document: open a PDF, Word, PowerPoint or Excel file a piece at a time. Built for long ones: ask for pages, search the
/// whole file for a word, or read a range of cells, without ever pulling the whole thing into her context.</summary>
internal sealed class ReadDocument : Tool
{
    private readonly FileSystem fs;
    private readonly string name;
    private readonly string? only;      // the one extension a per-format reader accepts; null for the general read_document
    private readonly string description;
    private readonly string[] extras;   // which of pages / find / sheet / range this reader offers

    internal ReadDocument(FileSystem fs) : this(fs, "read_document", null,
        "(Same as the per-format readers.) Read a PDF, Word (.docx), PowerPoint (.pptx) or Excel (.xlsx) file. With only a path it gives the page or sheet count and the opening; use find to search the whole file, pages to read parts, and for Excel sheet and range.",
        "pages", "find", "sheet", "range") { }

    internal ReadDocument(FileSystem fs, string name, string? only, string description, params string[] extras)
    { this.fs = fs; this.name = name; this.only = only; this.description = description; this.extras = extras; }

    internal override string     Name   => name;
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema
    {
        get
        {
            Dictionary<string, object> props = new() { ["path"] = new { type = "string", description = "The file's path in the project or working folder." } };
            if (extras.Contains("pages")) props["pages"] = new { type = "string", description = "Which pages, slides or sections: \"5\", \"5-8\" or \"1-3,9\". At most about 14,000 characters come back per call." };
            if (extras.Contains("find"))  props["find"]  = new { type = "string", description = "Search the whole file for this text and list where it appears (use this on a long file, then read just those parts)." };
            if (extras.Contains("sheet")) props["sheet"] = new { type = "string", description = "The sheet name (shown in the overview)." };
            if (extras.Contains("range")) props["range"] = new { type = "string", description = "Cells to read, e.g. A1:F50. Needs sheet." };
            return new { type = "function", function = new { name, description, parameters = new { type = "object", properties = props, required = new[] { "path" } } } };
        }
    }

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        string path = "", pages = "", find = "", sheet = "", range = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            string Get(string k) => doc.RootElement.TryGetProperty(k, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
            path = Get("path").Trim('"', '\'', ' '); pages = Get("pages"); find = Get("find"); sheet = Get("sheet"); range = Get("range");
        }
        catch (JsonException) { return "[Error: the arguments weren't valid JSON.]"; }
        if (path.Length == 0) return "[Error: give the path of the document.]";

        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        byte[] raw;
        try { raw = await fs.ReadBytes(path); }
        catch (Exception e) { return $"[Error: couldn't read {path}: {e.Message}]"; }
        // Judged by what the file starts with when its name doesn't say (an attachment saved without its extension).
        if (!DocumentReader.Extensions.Contains(ext)) ext = Read.DocumentKind(raw) ?? ext;
        if (!DocumentReader.Extensions.Contains(ext)) return $"[Error: that isn't a PDF, Word, PowerPoint or Excel file; use read_file for .{ext}.]";
        if (only is not null && ext != only) return $"[Error: {name} opens .{only} files; this is .{ext}. Use read_{ext} (or read_file) instead.]";

        try
        {
            if (ext == "xlsx" && sheet.Length > 0) return ReadCells(raw, sheet, range);
            Doc d = DocumentReader.Open(raw, ext);
            if (find.Length > 0) return DocumentReader.Find(d, find);
            if (pages.Length == 0) return DocumentReader.Overview(d, path);

            List<int> wanted = DocumentReader.ParsePages(pages, d.Parts.Count);
            if (wanted.Count == 0) return $"[Error: {Path.GetFileName(path)} has {d.Parts.Count} parts; \"{pages}\" names none of them.]";
            // A scanned page has no text, only a picture of the page: hand her the picture so she can read it with her own eyes.
            if (ext == "pdf" && wanted.All(n => d.Parts[n - 1].Text.Trim().Length == 0) && ScannedPage(raw, wanted[0]) is { } picture)
                return ToolResult.AsImage(picture.Bytes, picture.Mime);
            StringBuilder sb = new();
            foreach (int n in wanted)
            {
                DocPart p = d.Parts[n - 1];
                string block = $"── {p.Label} ──\n{(p.Text.Length == 0 ? "(no text)" : p.Text)}\n";
                if (sb.Length + block.Length > 14_000 && sb.Length > 0) { sb.AppendLine($"… stopped before {p.Label} to keep this readable. Continue with pages=\"{n}-{Math.Min(d.Parts.Count, n + 5)}\"."); break; }
                sb.AppendLine(block.Length > 14_000 ? block[..14_000] + "\n… (cut)" : block);
            }
            if (d.Note.Length > 0) sb.AppendLine(d.Note);
            return sb.ToString().TrimEnd();
        }
        catch (Exception e) { return $"[Error: couldn't open {Path.GetFileName(path)}: {e.Message}]"; }
    }

    private static (byte[] Bytes, string Mime)? ScannedPage(byte[] raw, int page)
    {
        try
        {
            using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(raw);
            var biggest = pdf.GetPage(page).GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples).FirstOrDefault();
            if (biggest is null) return null;
            if (biggest.TryGetPng(out byte[]? png) && png is { Length: > 0 }) return (png, "image/png");
            byte[] bytes = biggest.RawBytes.ToArray();
            return bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? (bytes, "image/jpeg") : null;
        }
        catch { return null; }
    }

    private static string ReadCells(byte[] raw, string sheet, string range)
    {
        using MemoryStream ms = new(raw);
        using XLWorkbook wb = new(ms);
        if (!wb.TryGetWorksheet(sheet, out IXLWorksheet? ws))
            return $"[Error: there is no sheet called \"{sheet}\". The sheets are: {string.Join(", ", wb.Worksheets.Select(w => $"\"{w.Name}\""))}.]";
        IXLRange? used = ws.RangeUsed();
        if (used is null) return $"Sheet \"{sheet}\" is empty.";
        IXLRange r;
        try { r = range.Length > 0 ? ws.Range(range) : ws.Range(used.RangeAddress.FirstAddress, used.RangeAddress.LastAddress); }
        catch { return $"[Error: \"{range}\" isn't a range. Use something like A1:F50.]"; }
        int last = Math.Min(r.LastRow().RowNumber(), r.FirstRow().RowNumber() + 199);
        string text = DocumentReader.SheetText(ws, r.FirstRow().RowNumber(), last, r.FirstColumn().ColumnNumber(), r.LastColumn().ColumnNumber());
        return $"Sheet \"{sheet}\", {r.RangeAddress} (column letters start at {r.FirstColumn().ColumnLetter()}; each line begins with its row number):\n{text}" +
               (last < r.LastRow().RowNumber() ? $"\n… {r.LastRow().RowNumber() - last} more rows; ask for the next range." : "");
    }
}
