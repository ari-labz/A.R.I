using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using HtmlAgilityPack;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ARI.LLM;

[Tool("convert_document", "Convert a document to another format: Word, PDF, PowerPoint, Excel, CSV, Markdown, HTML, text or a picture. " +
                          "From: docx, pdf, pptx, xlsx, csv, md, html, txt, png, jpg. To: md, txt, html, pdf, docx, csv, xlsx. " +
                          "It carries over the text, headings, lists and tables, not the exact page layout, fonts or colours, so tell the user when layout matters. " +
                          "Examples: docx to pdf, xlsx to csv (one file per sheet), csv to xlsx, md to docx, pdf to md, a picture to pdf. Saves a new file; the original is never changed.", Group = "convert_tools")]
public class ConvertDocument : Tool
{
    [Param("The file to convert, in your working folder or an absolute path.")]
    public string Path;

    [Param("The format to convert to: md, txt, html, pdf, docx, csv or xlsx.")]
    public string To;

    [Param("Where to save the result. Defaults to the same name with the new extension.", Optional = true)]
    public string SaveAs;

    public override async Task<string> Run()
    {
        string src = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(WorkingFolder, Path));
        if (!File.Exists(src)) return $"[Error: {src} doesn't exist.]";
        string from = System.IO.Path.GetExtension(src).TrimStart('.').ToLowerInvariant();
        string to = (To ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (to == "markdown") to = "md"; if (from == "markdown") from = "md"; if (from == "htm") from = "html"; if (from == "jpeg") from = "jpg";
        if (to is not ("md" or "txt" or "html" or "pdf" or "docx" or "csv" or "xlsx")) return "[Error: convert to md, txt, html, pdf, docx, csv or xlsx.]";
        if (from is not ("docx" or "pdf" or "pptx" or "xlsx" or "csv" or "md" or "html" or "txt" or "png" or "jpg")) return $"[Error: I can't convert from .{from}.]";
        if (from == to) return $"[Error: it is already a .{to} file.]";

        string name = string.IsNullOrWhiteSpace(SaveAs) ? System.IO.Path.GetFileNameWithoutExtension(src) + "." + to : SaveAs;
        string? refusal;
        string full = SaveRules.Resolve(WorkingFolder, name, "." + to, CallThread, out refusal);
        if (refusal is not null) return refusal;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string title = System.IO.Path.GetFileNameWithoutExtension(full);

        try
        {
            // Spreadsheets and CSV keep their cells; everything else goes through Markdown.
            if (from is "xlsx" or "csv" && to is "csv" or "xlsx")
                return ConvertTable(src, from, full, to);
            if (to is "csv" or "xlsx") return $"[Error: only spreadsheets and CSV files convert to .{to}. Use write_xlsx to build a workbook from a document's tables.]";

            string md = from switch
            {
                "md" or "txt" => await File.ReadAllTextAsync(src),
                "html" => HtmlToMarkdown(await File.ReadAllTextAsync(src)),
                "docx" => DocxToMarkdown(src),
                "xlsx" => XlsxToMarkdown(src),
                "csv" => CsvToMarkdown(await File.ReadAllTextAsync(src)),
                "pdf" or "pptx" => ReadParts(src, from),
                _ => $"![{System.IO.Path.GetFileNameWithoutExtension(src)}]({(src.StartsWith(System.IO.Path.GetFullPath(WorkingFolder)) ? System.IO.Path.GetRelativePath(WorkingFolder, src) : src)})",
            };
            if (md.Trim().Length == 0) return $"[Error: found no text in {System.IO.Path.GetFileName(src)}. If it is a scan, use ocr first.]";

            switch (to)
            {
                case "md": await File.WriteAllTextAsync(full, md); break;
                case "txt": await File.WriteAllTextAsync(full, ToPlainText(md)); break;
                case "html": await File.WriteAllTextAsync(full, MarkdownDoc.Html(md, title, WorkingFolder)); break;
                case "pdf": await File.WriteAllBytesAsync(full, await BrowserHost.PdfFromHtml(MarkdownDoc.Html(md, title, WorkingFolder))); break;
                case "docx": WordWriter.Write(full, md, WorkingFolder); break;
            }
            SaveRules.Created(CallThread, full);
            return $"Converted {System.IO.Path.GetFileName(src)} to {full}. Text, headings, lists and tables are carried over; the exact layout, fonts and colours are not. Give it to the user with deliver_file.";
        }
        catch (Exception e) { return $"[Error: couldn't convert {System.IO.Path.GetFileName(src)}: {e.Message}]"; }
    }

    private string ConvertTable(string src, string from, string full, string to)
    {
        if (from == "csv")
        {
            using XLWorkbook wb = new(); IXLWorksheet ws = wb.AddWorksheet("Sheet1"); int row = 0;
            foreach (string line in File.ReadAllText(src).Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0) continue; row++;
                List<string> cells = Xlsx.SplitCsv(line);
                for (int c = 0; c < cells.Count; c++) Xlsx.SetValue(ws.Cell(row, c + 1), cells[c]);
            }
            ws.RangeUsed()?.FirstRow().Style.Font.Bold = true; ws.Columns().AdjustToContents(1, 60);
            wb.SaveAs(full); SaveRules.Created(CallThread, full);
            return $"Converted to {full} ({row} rows). Give it to the user with deliver_file.";
        }
        using XLWorkbook book = new(src);
        List<string> written = new();
        foreach (IXLWorksheet s in book.Worksheets)
        {
            string target = book.Worksheets.Count == 1 ? full : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, $"{System.IO.Path.GetFileNameWithoutExtension(full)} - {s.Name}.csv");
            if (File.Exists(target) && CallThread is not null && !CallThread.CreatedFiles.Contains(target)) return $"[Blocked: {System.IO.Path.GetFileName(target)} was here before this conversation, so it isn't overwritten. Use save_as with a new name.]";
            StringBuilder sb = new();
            if (s.RangeUsed() is { } used)
                foreach (IXLRangeRow r in used.Rows())
                    sb.AppendLine(string.Join(",", r.Cells().Select(c => Csv(c.GetFormattedString()))));
            File.WriteAllText(target, sb.ToString()); SaveRules.Created(CallThread, target); written.Add(target);
        }
        return $"Converted to {written.Count} CSV file{(written.Count == 1 ? "" : "s")}:\n{string.Join("\n", written)}\nGive them to the user with deliver_file.";
    }

    private static string Csv(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static string MdCell(string v) => v.Replace("|", "\\|").Replace("\n", " ").Trim();

    private static string Table(List<List<string>> rows)
    {
        if (rows.Count == 0) return "";
        int width = rows.Max(r => r.Count);
        StringBuilder sb = new();
        for (int i = 0; i < rows.Count; i++)
        {
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, width).Select(c => c < rows[i].Count ? MdCell(rows[i][c]) : ""))).AppendLine(" |");
            if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).AppendLine();
        }
        return sb.ToString();
    }

    private static string CsvToMarkdown(string csv) => Table(csv.Replace("\r", "").Split('\n').Where(l => l.Length > 0).Select(Xlsx.SplitCsv).ToList());

    private static string XlsxToMarkdown(string path)
    {
        using XLWorkbook wb = new(path);
        StringBuilder sb = new();
        foreach (IXLWorksheet s in wb.Worksheets)
        {
            sb.AppendLine($"## {s.Name}").AppendLine();
            if (s.RangeUsed() is { } used) sb.AppendLine(Table(used.Rows().Select(r => r.Cells().Select(c => c.GetFormattedString()).ToList()).ToList()));
        }
        return sb.ToString();
    }

    private static string ReadParts(string path, string ext)
    {
        Doc d = DocumentReader.Open(File.ReadAllBytes(path), ext);
        StringBuilder sb = new();
        foreach (DocPart p in d.Parts)
        {
            if (p.Text.Trim().Length == 0) continue;
            sb.AppendLine(ext == "pptx" ? $"# {p.Label}" : d.Parts.Count > 1 ? $"## {p.Label}" : "").AppendLine().AppendLine(p.Text.Trim()).AppendLine();
        }
        return sb.ToString();
    }

    private static string ToPlainText(string md)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(md, @"^#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\*\*(.+?)\*\*|\*(.+?)\*", "$1$2");
        return System.Text.RegularExpressions.Regex.Replace(t, @"!?\[([^\]]*)\]\(([^)]*)\)", "$1 ($2)");
    }

    private static string DocxToMarkdown(string path)
    {
        using WordprocessingDocument doc = WordprocessingDocument.Open(path, false);
        W.Body body = doc.MainDocumentPart?.Document.Body ?? throw new InvalidDataException("that Word file has no body");
        StringBuilder sb = new();
        foreach (var el in body.ChildElements)
        {
            if (el is W.Paragraph p)
            {
                string text = ParagraphText(p).Trim();
                if (text.Length == 0) continue;
                string style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                int level = style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1
                    : style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out int h) ? Math.Clamp(h, 1, 6) : 0;
                if (level > 0) sb.AppendLine($"{new string('#', level)} {text}").AppendLine();
                else if (p.ParagraphProperties?.NumberingProperties is not null || style.Contains("List", StringComparison.OrdinalIgnoreCase)) sb.AppendLine($"- {text}");
                else sb.AppendLine(text).AppendLine();
            }
            else if (el is W.Table t)
                sb.AppendLine().AppendLine(Table(t.Elements<W.TableRow>().Select(r => r.Elements<W.TableCell>().Select(c => string.Join(" ", c.Elements<W.Paragraph>().Select(ParagraphText))).ToList()).ToList()));
        }
        return sb.ToString();
    }

    private static string ParagraphText(W.Paragraph p)
    {
        StringBuilder sb = new();
        foreach (W.Run r in p.Descendants<W.Run>())
        {
            string t = string.Concat(r.ChildElements.Select(c => c is W.Text x ? x.Text : c is W.TabChar ? "\t" : c is W.Break ? " " : ""));
            if (t.Trim().Length == 0) { sb.Append(t); continue; }
            bool bold = r.RunProperties?.Bold is { } b && b.Val?.Value != false, italic = r.RunProperties?.Italic is { } i && i.Val?.Value != false;
            string mark = (bold ? "**" : "") + (italic ? "*" : "");
            string rev = new(mark.Reverse().ToArray());
            sb.Append(mark.Length == 0 ? t : mark + t.Trim() + rev + (t.EndsWith(' ') ? " " : ""));
        }
        return sb.ToString();
    }

    private static string HtmlToMarkdown(string html)
    {
        HtmlDocument doc = new(); doc.LoadHtml(html);
        foreach (HtmlNode junk in doc.DocumentNode.SelectNodes("//script|//style|//nav|//noscript")?.ToList() ?? new List<HtmlNode>()) junk.Remove();
        StringBuilder sb = new();
        void Walk(HtmlNode n)
        {
            switch (n.Name.ToLowerInvariant())
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": sb.AppendLine().AppendLine($"{new string('#', n.Name[1] - '0')} {Clean(n)}").AppendLine(); return;
                case "p": sb.AppendLine(Clean(n)).AppendLine(); return;
                case "li": sb.AppendLine($"- {Clean(n)}"); return;
                case "br": sb.AppendLine(); return;
                case "hr": sb.AppendLine("---").AppendLine(); return;
                case "table":
                    sb.AppendLine().AppendLine(Table(n.Descendants("tr").Select(r => r.Elements("th").Concat(r.Elements("td")).Select(c => Clean(c)).ToList()).ToList()));
                    return;
                case "#text": if (n.InnerText.Trim().Length > 0 && n.ParentNode.Name is "body" or "div" or "section" or "article" or "main" or "#document") sb.AppendLine(HtmlEntity.DeEntitize(n.InnerText.Trim())).AppendLine(); return;
            }
            foreach (HtmlNode c in n.ChildNodes) Walk(c);
        }
        string Clean(HtmlNode n) => System.Text.RegularExpressions.Regex.Replace(HtmlEntity.DeEntitize(n.InnerText), @"\s+", " ").Trim();
        Walk(doc.DocumentNode);
        return sb.ToString();
    }
}
