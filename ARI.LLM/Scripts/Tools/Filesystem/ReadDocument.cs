using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace ARI.LLM;

/// <summary>read_document: open a PDF, Word, PowerPoint or Excel file a piece at a time. Built for long ones: ask for pages, search the
/// whole file for a word, or read a range of cells, without ever pulling the whole thing into her context.</summary>
internal sealed class ReadDocument : Tool
{
    private readonly FileSystem fs;
    internal ReadDocument(FileSystem fs) => this.fs = fs;

    internal override string     Name   => "read_document";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "read_document",
            description = "Read a PDF, Word (.docx), PowerPoint (.pptx) or Excel (.xlsx) file. With only a path it gives the page or sheet count and the opening. " +
                          "For a long document use find to locate a word or phrase across the whole file (it names the pages), then pages to read just those. " +
                          "For a spreadsheet, name a sheet and a range such as A1:F50. Never read a whole long document; look for the part you need.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path  = new { type = "string", description = "The file's path in the project or working folder." },
                    pages = new { type = "string", description = "Which pages, slides or sections: \"5\", \"5-8\" or \"1-3,9\". At most about 14,000 characters come back per call." },
                    find  = new { type = "string", description = "Search the whole document for this text and list where it appears." },
                    sheet = new { type = "string", description = "Excel only: the sheet name." },
                    range = new { type = "string", description = "Excel only: cells to read, e.g. A1:F50. Needs a sheet." },
                },
                required = new[] { "path" }
            }
        }
    };

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
        if (!DocumentReader.Extensions.Contains(ext)) return $"[Error: read_document opens .pdf, .docx, .pptx and .xlsx files; use read_file for .{ext}.]";

        byte[] raw;
        try { raw = await fs.ReadBytes(path); }
        catch (Exception e) { return $"[Error: couldn't read {path}: {e.Message}]"; }

        try
        {
            if (ext == "xlsx" && sheet.Length > 0) return ReadCells(raw, sheet, range);
            Doc d = DocumentReader.Open(raw, ext);
            if (find.Length > 0) return DocumentReader.Find(d, find);
            if (pages.Length == 0) return DocumentReader.Overview(d, path);

            List<int> wanted = DocumentReader.ParsePages(pages, d.Parts.Count);
            if (wanted.Count == 0) return $"[Error: {Path.GetFileName(path)} has {d.Parts.Count} parts; \"{pages}\" names none of them.]";
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
