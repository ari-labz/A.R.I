using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ARI.LLM;

// Where a document tool may save. A file that was already there when the conversation started (an upload, the user's own CV) is never changed
// in place: the tool refuses and says to save under a new name. Files Ari made herself in this conversation can be rewritten freely.
internal static class SaveRules
{
    internal static string Resolve(string workingFolder, string path, string extension, Thread? thread, out string? refusal)
    {
        refusal = null;
        string full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(workingFolder, path));
        if (!full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        { refusal = $"[Error: the file name must end in {extension}.]"; return full; }
        if (File.Exists(full) && thread is not null && !thread.CreatedFiles.Contains(full))
            refusal = $"[Blocked: {System.IO.Path.GetFileName(full)} was here before this conversation (the user's own file), so it isn't overwritten. Save under a new name instead, e.g. \"{System.IO.Path.GetFileNameWithoutExtension(full)} - improved{extension}\".]";
        return full;
    }

    internal static void Created(Thread? thread, string full) => thread?.CreatedFiles.Add(full);
}

internal static class AfterWriting
{
    /// <summary>Added to what a document writer returns, so the check happens just as she finishes writing, when it matters.</summary>
    internal const string Check = " Before you hand it over, check it against what the user gave you: anything you added or assumed (a job title, a skill, a duration, a number, a licence type) and every figure you worked out yourself must be removed, fixed, or named in your reply.";
}

internal static class WordWriter
{
    internal static void Write(string path, string markdown)
    {
        using WordprocessingDocument doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        MainDocumentPart main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main); AddNumbering(main);
        Body body = main.Document.Body!;
        foreach (Block b in MarkdownDoc.Parse(markdown)) body.Append(Build(main, b));
        body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708 }));
        main.Document.Save();
    }

    private static IEnumerable<OpenXmlElement> Build(MainDocumentPart main, Block b)
    {
        switch (b.Kind)
        {
            case BlockKind.Heading: yield return Para(main, b.Runs, style: $"Heading{b.Level}"); break;
            case BlockKind.Paragraph: yield return Para(main, b.Runs); break;
            case BlockKind.Bullet: yield return Para(main, b.Runs, style: "ListParagraph", num: 1, level: b.Level); break;
            case BlockKind.Number: yield return Para(main, b.Runs, style: "ListParagraph", num: 2, level: b.Level); break;
            case BlockKind.Rule:
                yield return new Paragraph(new ParagraphProperties(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "BBBBBB", Space = 1 })));
                break;
            case BlockKind.PageBreak: yield return new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new Break { Type = BreakValues.Page })); break;
            case BlockKind.Table:
                Table t = new(new TableProperties(new TableStyle { Val = "TableGrid" }, new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));
                for (int r = 0; r < b.Rows.Count; r++)
                {
                    TableRow row = new();
                    foreach (List<Run> cell in b.Rows[r])
                    {
                        List<Run> runs = r == 0 ? cell.Select(x => x with { Bold = true }).ToList() : cell;
                        TableCell tc = new(Para(main, runs));
                        if (r == 0) tc.TableCellProperties = new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "EEF2F4" });
                        row.Append(tc);
                    }
                    t.Append(row);
                }
                yield return t; yield return new Paragraph();
                break;
        }
    }

    private static Paragraph Para(MainDocumentPart main, IEnumerable<ARI.LLM.Run> runs, string? style = null, int num = 0, int level = 0)
    {
        ParagraphProperties pp = new();
        if (style is not null) pp.ParagraphStyleId = new ParagraphStyleId { Val = style };
        if (num > 0) pp.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = num });
        Paragraph p = new(pp);
        foreach (ARI.LLM.Run r in runs)
        {
            RunProperties rp = new();
            if (r.Bold) rp.Bold = new Bold();
            if (r.Italic) rp.Italic = new Italic();
            if (r.Code) rp.RunFonts = new RunFonts { Ascii = "Menlo", HighAnsi = "Menlo" };
            DocumentFormat.OpenXml.Wordprocessing.Run wr = new(rp, new Text(r.Text) { Space = SpaceProcessingModeValues.Preserve });
            if (r.Link is { } url)
            {
                rp.Color = new Color { Val = "0563C1" }; rp.Underline = new Underline { Val = UnderlineValues.Single };
                string id = main.AddHyperlinkRelationship(new Uri(url, UriKind.RelativeOrAbsolute), true).Id;
                p.Append(new Hyperlink(wr) { Id = id });
            }
            else p.Append(wr);
        }
        return p;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        Styles styles = new(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "100", Line = "264", LineRule = LineSpacingRuleValues.Auto }))));
        styles.Append(new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        (string id, string size, string before)[] heads = { ("Heading1", "40", "0"), ("Heading2", "30", "280"), ("Heading3", "25", "200") };
        foreach (var (id, size, before) in heads)
            styles.Append(new Style(new StyleName { Val = id.Replace("Heading", "heading ") }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" },
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = before, After = "80" }),
                new StyleRunProperties(new Bold(), new FontSize { Val = size }, new Color { Val = "1F3A45" })) { Type = StyleValues.Paragraph, StyleId = id });
        styles.Append(new Style(new StyleName { Val = "List Paragraph" }, new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "40" })) { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });
        styles.Append(new Style(new StyleName { Val = "Table Grid" },
            new StyleTableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" }, new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" }, new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" }),
                new TableCellMarginDefault(new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa }, new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa }))) { Type = StyleValues.Table, StyleId = "TableGrid" });
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        Level L(int i, NumberFormatValues fmt, string text) => new(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = fmt }, new LevelText { Val = text },
            new PreviousParagraphProperties(new Indentation { Left = (720 + i * 360).ToString(), Hanging = "360" })) { LevelIndex = i };
        Numbering n = new(
            new AbstractNum(L(0, NumberFormatValues.Bullet, "•"), L(1, NumberFormatValues.Bullet, "–"), L(2, NumberFormatValues.Bullet, "•")) { AbstractNumberId = 1 },
            new AbstractNum(L(0, NumberFormatValues.Decimal, "%1."), L(1, NumberFormatValues.LowerLetter, "%2."), L(2, NumberFormatValues.LowerRoman, "%3.")) { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 }, new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = n;
    }

    /// <summary>Edits applied to a copy of a Word file, so the original's formatting and everything else in it stays as it was.</summary>
    internal static (int Done, List<string> Problems) Edit(string path, IEnumerable<string> edits)
    {
        using WordprocessingDocument doc = WordprocessingDocument.Open(path, true);
        MainDocumentPart main = doc.MainDocumentPart!;
        Body body = main.Document.Body!;
        int done = 0; List<string> problems = new();
        foreach (string raw in edits)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                if (line.StartsWith("replace ", StringComparison.OrdinalIgnoreCase) && line.Contains("=>"))
                {
                    int at = line.IndexOf("=>", StringComparison.Ordinal);
                    string find = line[8..at].Trim().Trim('"'), with = line[(at + 2)..].Trim().Trim('"');
                    int n = ReplaceText(body, find, with);
                    if (n == 0) problems.Add($"\"{find}\" isn't in the document"); else done++;
                }
                else if (line.StartsWith("delete ", StringComparison.OrdinalIgnoreCase))
                {
                    string find = line[7..].Trim().Trim('"');
                    List<Paragraph> hit = body.Descendants<Paragraph>().Where(p => p.InnerText.Contains(find, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hit.Count == 0) problems.Add($"no paragraph contains \"{find}\""); else { foreach (Paragraph p in hit) p.Remove(); done++; }
                }
                else if (line.StartsWith("append ", StringComparison.OrdinalIgnoreCase))
                {
                    SectionProperties? sect = body.Elements<SectionProperties>().LastOrDefault();
                    foreach (Block b in MarkdownDoc.Parse(line[7..].Replace("\\n", "\n")))
                        foreach (OpenXmlElement el in Build(main, b)) { if (sect is not null) body.InsertBefore(el, sect); else body.Append(el); }
                    done++;
                }
                else problems.Add($"\"{line}\": use  replace OLD => NEW,  delete TEXT,  or  append MARKDOWN");
            }
            catch (Exception e) { problems.Add($"\"{line}\": {e.Message}"); }
        }
        main.Document.Save();
        return (done, problems);
    }

    // Text in Word is split into runs wherever formatting changes, so a phrase can span several. Find it in the paragraph's joined text and
    // rewrite it into the first run it touches, leaving the others' formatting alone.
    private static int ReplaceText(Body body, string find, string with)
    {
        int count = 0;
        foreach (Paragraph p in body.Descendants<Paragraph>())
        {
            List<Text> texts = p.Descendants<Text>().ToList();
            string joined = string.Concat(texts.Select(t => t.Text));
            int idx;
            while ((idx = joined.IndexOf(find, StringComparison.Ordinal)) >= 0)
            {
                int pos = 0, end = idx + find.Length; bool placed = false;
                foreach (Text t in texts)
                {
                    int start = pos, stop = pos + t.Text.Length; pos = stop;
                    if (stop <= idx || start >= end) continue;
                    int from = Math.Max(idx, start) - start, to = Math.Min(end, stop) - start;
                    t.Text = t.Text[..from] + (placed ? "" : with) + t.Text[to..];
                    t.Space = SpaceProcessingModeValues.Preserve; placed = true;
                }
                joined = string.Concat(texts.Select(t => t.Text)); count++;
                if (with.Contains(find)) break;
            }
        }
        return count;
    }
}

[Tool("write_docx", "Create a Word document (.docx) from Markdown. Use it whenever the user asks for a Word document, CV, letter, report or any .docx. " +
                    "Markdown supported: # ## ### headings, paragraphs, - bullets (indent two spaces to nest), 1. numbered lists, **bold**, *italic*, [text](url), | tables | with a |---| row, --- for a divider, and a line \\pagebreak for a new page. " +
                    "Use only facts the user gave you; if you have to assume something, say so in your reply instead of putting it in the document. It saves into your working folder; give it to the user with deliver_file. " +
                    "It never overwrites a file that was there before the conversation: save a changed version under a new name.", Group = "docx_tools")]
public class WriteDocx : Tool
{
    [Param("Where to save it, ending in .docx, e.g. \"Jordan Pearce - CV.docx\".")]
    public string Path;

    [Param("The document as Markdown.")]
    public string Content;

    public override Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".docx", CallThread, out string? refusal);
        if (refusal is not null) return Task.FromResult(refusal);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        WordWriter.Write(full, Content);
        SaveRules.Created(CallThread, full);
        int words = Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Task.FromResult($"Saved {full} (about {words} words). Give it to the user with deliver_file." + AfterWriting.Check);
    }
}

[Tool("edit_docx", "Change an existing Word document and keep its formatting. One edit per line: \"replace OLD => NEW\" (text anywhere in the document), \"delete TEXT\" (removes every paragraph containing it), " +
                   "or \"append MARKDOWN\" (adds paragraphs at the end; use \\n for line breaks). Look at the file first with read_docx so you know what it says. " +
                   "If the file was there before this conversation it is left alone and your changes go to a copy (save_as), so the user's original is never altered.", Group = "docx_tools")]
public class EditDocx : Tool
{
    [Param("The Word document to change, ending in .docx.")]
    public string Path;

    [Param("The edits, one per line.")]
    public string Edits;

    [Param("Where to save the changed copy, ending in .docx. Needed when the original is the user's own file; defaults to \"<name> - edited.docx\".", Optional = true)]
    public string SaveAs;

    public override Task<string> Run()
    {
        string source = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(WorkingFolder, Path));
        if (!File.Exists(source)) return Task.FromResult($"[Error: {source} doesn't exist. Create it with write_docx.]");
        bool mine = CallThread is not null && CallThread.CreatedFiles.Contains(source);
        string target = source;
        if (!mine || !string.IsNullOrWhiteSpace(SaveAs))
        {
            string name = string.IsNullOrWhiteSpace(SaveAs) ? System.IO.Path.GetFileNameWithoutExtension(source) + " - edited.docx" : SaveAs;
            target = SaveRules.Resolve(WorkingFolder, name, ".docx", CallThread, out string? refusal);
            if (refusal is not null) return Task.FromResult(refusal);
            File.Copy(source, target, overwrite: true);
        }
        (int done, List<string> problems) = WordWriter.Edit(target, Edits.Replace("\r", "").Split('\n'));
        SaveRules.Created(CallThread, target);
        return Task.FromResult($"Applied {done} edit{(done == 1 ? "" : "s")}; saved {target}." + (problems.Count > 0 ? $"\nCouldn't apply:\n- {string.Join("\n- ", problems)}" : "") + " Give it to the user with deliver_file." + AfterWriting.Check);
    }
}
