using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using A = DocumentFormat.OpenXml.Drawing;

namespace ARI.LLM;

/// <summary>One page, slide, sheet or section of a document.</summary>
internal sealed record DocPart(string Label, string Text);

internal sealed record Doc(string Kind, List<DocPart> Parts, string Note);

/// <summary>Turns PDF, Word, PowerPoint and Excel files into plain text a model can read, split into parts (pages, slides,
/// sheets, or chunks of a Word file) so a long one can be read a piece at a time and searched. Pure managed code, nothing to install.</summary>
internal static class DocumentReader
{
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "pdf", "docx", "pptx", "xlsx" };

    private const int SheetRows = 100;
    private const int WordChunkChars = 3500;

    // Parsing a 500-page PDF takes a moment; reading it in windows should not repeat that on every call.
    private static readonly ConcurrentDictionary<string, Doc> cache = new();
    private static readonly ConcurrentQueue<string> order = new();

    internal static Doc Open(byte[] raw, string ext)
    {
        string key = ext + ":" + Convert.ToHexString(SHA256.HashData(raw));
        if (cache.TryGetValue(key, out Doc? hit)) return hit;
        Doc doc = ext.ToLowerInvariant() switch
        {
            "pdf"  => Pdf(raw),
            "docx" => Word(raw),
            "pptx" => Slides(raw),
            "xlsx" => Sheets(raw),
            _      => throw new NotSupportedException($".{ext} isn't a document ARI can open."),
        };
        cache[key] = doc; order.Enqueue(key);
        while (order.Count > 8 && order.TryDequeue(out string? old)) cache.TryRemove(old, out _);
        return doc;
    }

    private static Doc Pdf(byte[] raw)
    {
        List<DocPart> parts = new();
        using PdfDocument pdf = PdfDocument.Open(raw);
        foreach (UglyToad.PdfPig.Content.Page page in pdf.GetPages())
        {
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); } catch { text = page.Text; }
            parts.Add(new DocPart($"page {page.Number}", text.Trim()));
        }
        int empty = parts.Count(p => p.Text.Length < 20);
        string note = parts.Count > 0 && empty * 2 > parts.Count
            ? $"{empty} of {parts.Count} pages have no text layer: this looks like a scanned document. Ask for one page at a time (pages=\"1\") and you will be shown that page as a picture to read yourself."
            : "";
        return new Doc("PDF", parts, note);
    }

    private static Doc Word(byte[] raw)
    {
        using MemoryStream ms = new(raw);
        using WordprocessingDocument doc = WordprocessingDocument.Open(ms, false);
        StringBuilder sb = new();
        foreach (var el in doc.MainDocumentPart?.Document.Body?.ChildElements ?? default)
        {
            if (el is Paragraph p)
            {
                string text = p.InnerText;
                if (text.Length == 0) { sb.AppendLine(); continue; }
                string style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                sb.AppendLine(style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style["Heading".Length..], out int level) ? $"{new string('#', Math.Clamp(level, 1, 6))} {text}" : text);
            }
            else if (el is Table t)
                foreach (TableRow row in t.Elements<TableRow>())
                    sb.AppendLine(string.Join("\t", row.Elements<TableCell>().Select(c => c.InnerText.Replace('\n', ' '))));
        }
        // Word has no fixed pages, so it's cut into chunks at paragraph breaks.
        List<DocPart> parts = new();
        StringBuilder chunk = new();
        foreach (string line in sb.ToString().Split('\n'))
        {
            if (chunk.Length + line.Length > WordChunkChars && chunk.Length > 0) { parts.Add(new DocPart($"section {parts.Count + 1}", chunk.ToString().Trim())); chunk.Clear(); }
            chunk.AppendLine(line.TrimEnd('\r'));
        }
        if (chunk.ToString().Trim().Length > 0) parts.Add(new DocPart($"section {parts.Count + 1}", chunk.ToString().Trim()));
        return new Doc("Word document", parts, "");
    }

    private static Doc Slides(byte[] raw)
    {
        using MemoryStream ms = new(raw);
        using PresentationDocument pres = PresentationDocument.Open(ms, false);
        List<DocPart> parts = new();
        PresentationPart? pp = pres.PresentationPart;
        int n = 0;
        foreach (var id in pp?.Presentation.SlideIdList?.ChildElements.OfType<DocumentFormat.OpenXml.Presentation.SlideId>() ?? Enumerable.Empty<DocumentFormat.OpenXml.Presentation.SlideId>())
        {
            n++;
            SlidePart slide = (SlidePart)pp!.GetPartById(id.RelationshipId!);
            StringBuilder sb = new();
            foreach (var shape in slide.Slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
            {
                string text = string.Join("\n", shape.Descendants<A.Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0));
                if (text.Length > 0) sb.AppendLine(text);
            }
            string notes = slide.NotesSlidePart?.NotesSlide.InnerText ?? "";
            if (notes.Trim().Length > 0) sb.AppendLine($"[speaker notes] {notes.Trim()}");
            parts.Add(new DocPart($"slide {n}", sb.ToString().Trim()));
        }
        return new Doc("PowerPoint presentation", parts, "");
    }

    private static Doc Sheets(byte[] raw)
    {
        using MemoryStream ms = new(raw);
        using XLWorkbook wb = new(ms);
        List<DocPart> parts = new();
        foreach (IXLWorksheet ws in wb.Worksheets)
        {
            IXLRange? used = ws.RangeUsed();
            if (used is null) { parts.Add(new DocPart($"sheet \"{ws.Name}\"", "(empty)")); continue; }
            parts.Add(new DocPart($"sheet \"{ws.Name}\" ({used.RangeAddress})", SheetText(ws, used.FirstRow().RowNumber(), Math.Min(used.LastRow().RowNumber(), used.FirstRow().RowNumber() + SheetRows - 1), used.FirstColumn().ColumnNumber(), used.LastColumn().ColumnNumber())
                + (used.RowCount() > SheetRows ? $"\n… {used.RowCount() - SheetRows} more rows. Read them with read_document, sheet and range, e.g. range \"A{SheetRows + 1}:{used.LastColumn().ColumnLetter()}{SheetRows * 2}\"." : "")));
        }
        return new Doc("Excel workbook", parts, "");
    }

    /// <summary>Rows of a sheet as tab-separated text, each with its row number. Formulas show the value they calculate to, then the formula.</summary>
    internal static string SheetText(IXLWorksheet ws, int firstRow, int lastRow, int firstCol, int lastCol)
    {
        StringBuilder sb = new();
        for (int r = firstRow; r <= lastRow; r++)
        {
            List<string> cells = new();
            for (int c = firstCol; c <= lastCol; c++)
            {
                IXLCell cell = ws.Cell(r, c);
                string v = cell.GetFormattedString();
                cells.Add(cell.HasFormula ? $"{v} [={cell.FormulaA1.TrimStart('=')}]" : v);
            }
            sb.AppendLine($"{r}\t{string.Join("\t", cells)}");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Overview(Doc d, string path, int budget = 9000)
    {
        StringBuilder sb = new();
        sb.AppendLine($"{Path.GetFileName(path)}: {d.Kind}, {d.Parts.Count} {(d.Kind == "PDF" ? "pages" : d.Kind.StartsWith("Excel") ? "sheets" : d.Kind.StartsWith("PowerPoint") ? "slides" : "sections")}.");
        if (d.Note.Length > 0) sb.AppendLine(d.Note);
        sb.AppendLine("Read more with read_document: pages (e.g. \"5-8\") or find (search the whole file).");
        sb.AppendLine();
        int shown = 0;
        foreach (DocPart p in d.Parts)
        {
            string block = $"── {p.Label} ──\n{p.Text}\n";
            if (sb.Length + block.Length > budget && shown > 0) break;
            sb.AppendLine(block.Length > budget ? block[..budget] + "\n… (cut)" : block);
            shown++;
        }
        if (shown < d.Parts.Count) sb.AppendLine($"… showing {shown} of {d.Parts.Count}. Ask for the rest with pages=\"{shown + 1}-{Math.Min(d.Parts.Count, shown + 10)}\".");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Which parts a request like "3-7,12" names (1-based), clamped to what exists.</summary>
    internal static List<int> ParsePages(string spec, int count)
    {
        List<int> pages = new();
        foreach (string piece in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ends = piece.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out int a)) continue;
            int b = ends.Length == 2 && int.TryParse(ends[1], out int bb) ? bb : a;
            for (int i = Math.Max(1, a); i <= Math.Min(count, Math.Max(a, b)); i++) pages.Add(i);
        }
        return pages.Distinct().ToList();
    }

    internal static string Find(Doc d, string needle)
    {
        StringBuilder sb = new();
        int hits = 0;
        foreach (DocPart p in d.Parts)
        {
            int at = p.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            int count = 0, from = 0;
            while ((from = p.Text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; from += needle.Length; }
            string snippet = p.Text.Substring(Math.Max(0, at - 70), Math.Min(p.Text.Length - Math.Max(0, at - 70), 190)).Replace('\n', ' ');
            sb.AppendLine($"- {p.Label}{(count > 1 ? $" ({count} matches)" : "")}: …{snippet}…");
            if (++hits >= 25) { sb.AppendLine("… more matches not shown; search for something more specific."); break; }
        }
        return hits == 0 ? $"No match for \"{needle}\" in {d.Parts.Count} parts." : $"\"{needle}\" appears in:\n{sb.ToString().TrimEnd()}";
    }
}

/// <summary>A Word, PowerPoint, Excel or PDF file read as a type of read, so read_file opens one too. It returns the opening of the
/// file and points to read_document for the rest.</summary>
internal sealed class ReadOffice : Read
{
    internal ReadOffice(FileSystem fs) : base(fs) { }

    protected override ToolResult Decode(byte[] raw, string path)
    {
        try { return DocumentReader.Overview(DocumentReader.Open(raw, Extension(path)), path); }
        catch (Exception e) { return $"[Error: couldn't open {Path.GetFileName(path)} as a {Extension(path)} file: {e.Message}]"; }
    }
}
