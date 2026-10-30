using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace ARI.LLM;

[Tool("write_pdf", "Create a PDF from Markdown (same Markdown as write_docx: # headings, paragraphs, - bullets, 1. lists, **bold**, *italic*, [links](url), | tables |, --- dividers, a picture on its own line ![caption](chart.png) from your working folder, \\pagebreak). " +
                   "Use it for a PDF the user asked for: a handout, report, invoice, a CV as PDF. Use only facts the user gave you. It saves into your working folder; give it to the user with deliver_file. " +
                   "It never overwrites a file that was there before the conversation.", Group = "pdf_tools")]
public class WritePdf : Tool
{
    [Param("Where to save it, ending in .pdf.")]
    public string Path;

    [Param("The document as Markdown.")]
    public string Content;

    [Param("A title for the PDF's properties. Defaults to the file name.", Optional = true)]
    public string Title;

    public override async Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".pdf", CallThread, out string? refusal);
        if (refusal is not null) return refusal;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        byte[] pdf = await BrowserHost.PdfFromHtml(MarkdownDoc.Html(Content, string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(full) : Title, WorkingFolder));
        await File.WriteAllBytesAsync(full, pdf);
        SaveRules.Created(CallThread, full);
        using PdfDocument check = PdfDocument.Open(pdf);
        return $"Saved {full} ({check.NumberOfPages} page{(check.NumberOfPages == 1 ? "" : "s")}). Give it to the user with deliver_file." + AfterWriting.Check;
    }
}

[Tool("pdf_merge", "Join several PDFs into one, in the order given. Saves a new file; the originals are untouched.", Group = "pdf_tools")]
public class PdfMerge : Tool
{
    [Param("The PDFs to join, one path per line, in order.")]
    public string Files;

    [Param("Where to save the result, ending in .pdf.")]
    public string Path;

    public override Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".pdf", CallThread, out string? refusal);
        if (refusal is not null) return Task.FromResult(refusal);
        List<byte[]> inputs = new();
        foreach (string f in Files.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(f) ? f : System.IO.Path.Combine(WorkingFolder, f));
            if (!File.Exists(p)) return Task.FromResult($"[Error: {f} doesn't exist.]");
            inputs.Add(File.ReadAllBytes(p));
        }
        if (inputs.Count < 2) return Task.FromResult("[Error: give at least two PDFs to join.]");
        File.WriteAllBytes(full, PdfMerger.Merge(inputs));
        SaveRules.Created(CallThread, full);
        return Task.FromResult($"Joined {inputs.Count} PDFs into {full}. Give it to the user with deliver_file.");
    }
}

[Tool("pdf_extract_pages", "Make a new PDF from some pages of another (to split one up, drop pages, or reorder). Pages are 1-based, e.g. \"1-3,7,5\" (the order given is the order kept). The original is untouched.", Group = "pdf_tools")]
public class PdfExtractPages : Tool
{
    [Param("The source PDF.")]
    public string Source;

    [Param("Which pages to keep, e.g. \"1-3,7\".")]
    public string Pages;

    [Param("Where to save the result, ending in .pdf.")]
    public string Path;

    public override Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".pdf", CallThread, out string? refusal);
        if (refusal is not null) return Task.FromResult(refusal);
        string src = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Source) ? Source : System.IO.Path.Combine(WorkingFolder, Source));
        if (!File.Exists(src)) return Task.FromResult($"[Error: {Source} doesn't exist.]");
        byte[] bytes = File.ReadAllBytes(src);
        int total; using (PdfDocument d = PdfDocument.Open(bytes)) total = d.NumberOfPages;
        List<int> keep = new();
        foreach (string part in Pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split('-');
            if (!int.TryParse(ends[0], out int a) || (ends.Length > 1 && !int.TryParse(ends[1], out _))) return Task.FromResult($"[Error: \"{part}\" isn't a page or range.]");
            int b = ends.Length > 1 ? int.Parse(ends[1]) : a;
            for (int p = Math.Min(a, b); p <= Math.Max(a, b); p++) { if (p < 1 || p > total) return Task.FromResult($"[Error: page {p} doesn't exist; the PDF has {total} pages.]"); keep.Add(p); }
        }
        if (keep.Count == 0) return Task.FromResult("[Error: no pages chosen.]");
        File.WriteAllBytes(full, PdfMerger.Merge(new List<byte[]> { bytes }, new List<IReadOnlyList<int>> { keep }));
        SaveRules.Created(CallThread, full);
        return Task.FromResult($"Saved {keep.Count} page{(keep.Count == 1 ? "" : "s")} of {total} to {full}. Give it to the user with deliver_file.");
    }
}
