using System.Text;
using System.Text.Json;

namespace ARI.LLM;

[Tool("ocr", "Read the text out of a picture or a scanned PDF: a screenshot, a photo of a page, a receipt, a scanned form. It gives back the words it finds, line by line. " +
             "Use it when a page has no text layer (read_pdf comes back empty) or when you need the exact words from an image; to understand what a picture shows, open it with read_file instead. " +
             "Handwriting and very small print can come out wrong, so check numbers against the picture. The first use sets up a small Python environment, which takes a few minutes.", Group = "image_tools")]
public class Ocr : Tool
{
    [Param("A picture (.png, .jpg, .webp, .bmp, .gif) or a PDF, in your working folder or an absolute path.")]
    public string Path;

    [Param("For a PDF: which pages, e.g. \"1\", \"2-4\" or \"1,3\". Default is the first 5 pages.", Optional = true)]
    public string Pages;

    public override async Task<string> Run()
    {
        string full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(WorkingFolder, Path));
        if (!File.Exists(full)) return $"[Error: {full} doesn't exist.]";
        string ext = System.IO.Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        if (ext is not ("png" or "jpg" or "jpeg" or "webp" or "bmp" or "gif" or "pdf")) return "[Error: ocr reads pictures (png, jpg, webp, bmp, gif) and PDFs.]";

        await DocumentVenv.EnsurePackage("rapidocr-onnxruntime");
        await DocumentVenv.EnsurePackage("pypdfium2");
        string payload = JsonSerializer.Serialize(new { path = full, pages = Pages ?? "" });
        (int code, string output, string err) = await DocumentVenv.RunScript(Source, payload, WorkingFolder, "ocr_run.py");
        if (code != 0) return $"[Error: couldn't read that: {(err.Length > 0 ? err.Trim().Split('\n').Last() : output.Trim())}]";

        using JsonDocument doc = JsonDocument.Parse(output.Trim().Split('\n').Last());
        StringBuilder sb = new();
        int total = doc.RootElement.GetProperty("total").GetInt32();
        foreach (JsonElement p in doc.RootElement.GetProperty("pages").EnumerateArray())
        {
            string text = p.GetProperty("text").GetString() ?? "";
            if (total > 1 || ext == "pdf") sb.AppendLine($"── page {p.GetProperty("page").GetInt32()} (confidence {p.GetProperty("confidence").GetDouble():P0}) ──");
            else sb.AppendLine($"(confidence {p.GetProperty("confidence").GetDouble():P0})");
            sb.AppendLine(text.Length == 0 ? "(no text found)" : text).AppendLine();
        }
        if (total > doc.RootElement.GetProperty("pages").GetArrayLength()) sb.AppendLine($"The PDF has {total} pages; ask for others with pages.");
        string result = sb.ToString().TrimEnd();
        return result.Length > 14_000 ? result[..14_000] + "\n… (cut: ask for fewer pages at a time)" : result;
    }

    private const string Source = """
import sys, json
from rapidocr_onnxruntime import RapidOCR

cfg = json.load(sys.stdin)
engine = RapidOCR()

def read(img):
    result, _ = engine(img)
    if not result: return "", 0.0
    items = []
    for box, text, conf in result:
        ys = [p[1] for p in box]; xs = [p[0] for p in box]
        items.append((min(ys), max(ys), min(xs), text, float(conf)))
    items.sort(key=lambda t: (t[0], t[2]))
    rows = []
    for it in items:
        height = max(it[1] - it[0], 1)
        if rows and abs(it[0] - rows[-1][0]) < height * 0.6: rows[-1][1].append(it)
        else: rows.append([it[0], [it]])
    lines = [" ".join(t[3] for t in sorted(r[1], key=lambda t: t[2])) for r in rows]
    return "\n".join(lines), sum(t[4] for t in items) / len(items)

def parse_pages(spec, count):
    if not spec.strip(): return list(range(1, min(count, 5) + 1))
    out = []
    for part in spec.split(","):
        part = part.strip()
        if "-" in part:
            a, b = part.split("-", 1); out += range(int(a), int(b) + 1)
        elif part: out.append(int(part))
    return [n for n in out if 1 <= n <= count][:10]

pages = []
if cfg["path"].lower().endswith(".pdf"):
    import pypdfium2 as pdfium
    pdf = pdfium.PdfDocument(cfg["path"])
    total = len(pdf)
    for n in parse_pages(cfg["pages"], total):
        text, conf = read(pdf[n - 1].render(scale=2.5).to_numpy())
        pages.append({"page": n, "text": text, "confidence": conf})
else:
    total = 1
    text, conf = read(cfg["path"])
    pages.append({"page": 1, "text": text, "confidence": conf})
print(json.dumps({"total": total, "pages": pages}))
""";
}
