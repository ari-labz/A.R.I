using System.Diagnostics;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// PowerPoint files need real layout code, so they're built by python-pptx inside a venv ARI makes for itself on first use (like the voice and
// listener venvs). Nothing is installed by hand; a fresh install does the same one-time setup.
internal static class DocumentVenv
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    internal static string Folder => Path.Combine(Paths.AppData, "venvs", "documents");
    internal static string Python => Path.Combine(Folder, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
    private static string Marker => Path.Combine(Folder, ".ari-ready");

    internal static async Task<string> Ensure()
    {
        await gate.WaitAsync();
        try
        {
            if (File.Exists(Marker)) return Python;
            Directory.CreateDirectory(Path.GetDirectoryName(Folder)!);
            Shared.Logger.LogInformation("[Documents] Setting up ARI's document tools (one time: a small Python environment with python-pptx)…");
            if (!File.Exists(Python)) await Run(PythonLocator.Find(), $"-m venv \"{Folder}\"");
            await Run(Python, "-m pip install -q --disable-pip-version-check python-pptx");
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("O"));
            return Python;
        }
        finally { gate.Release(); }
    }

    /// <summary>Adds a Python package to the document environment once (the chart tool needs matplotlib), the same way the first setup does.</summary>
    internal static async Task EnsurePackage(string package)
    {
        string py = await Ensure();
        string marker = Path.Combine(Folder, $".ari-{package}");
        if (File.Exists(marker)) return;
        await gate.WaitAsync();
        try
        {
            if (File.Exists(marker)) return;
            Shared.Logger.LogInformation("[Documents] Adding {Package} to ARI's document tools (one time)…", package);
            await Run(py, $"-m pip install -q --disable-pip-version-check {package}");
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        finally { gate.Release(); }
    }

    internal static async Task<(int Code, string Out, string Err)> RunScript(string script, string stdin, string workingFolder, string scriptName = "pptx_build.py")
    {
        string py = await Ensure();
        string file = Path.Combine(Folder, "scripts"); Directory.CreateDirectory(file); file = Path.Combine(file, scriptName);
        File.WriteAllText(file, script);
        ProcessStartInfo psi = new(py) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingFolder };
        psi.ArgumentList.Add(file);
        using Process p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync(), e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return (-1, "", "Timed out after 2 minutes."); }
        return (p.ExitCode, await o, await e);
    }

    private static async Task Run(string exe, string args)
    {
        using Process p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException($"Couldn't start {exe}. Install Python 3 (or set ARI_PYTHON) so ARI can set up its document tools.");
        Task<string> err = p.StandardError.ReadToEndAsync(); await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"Setting up the document tools failed: {(await err).Trim()}");
    }
}

[Tool("write_pptx", "Create a PowerPoint deck (.pptx) from Markdown. Each slide starts with a \"# Slide title\" line. Under it: - bullets (indent two spaces to nest), plain lines as a paragraph, " +
                    "a | table | with a |---| row, an image as ![caption](picture.png) from your working folder, and speaker notes after a line \"notes:\". " +
                    "The first slide is the title slide: its title, then one line of subtitle. Use only facts the user gave you. Keep slides short, about five bullets at most. " +
                    "It saves into your working folder; give it to the user with deliver_file. It never overwrites a file that was there before the conversation. " +
                    "The first use sets up a small Python environment, which takes a minute.", Group = "pptx_tools")]
public class WritePptx : Tool
{
    [Param("Where to save it, ending in .pptx.")]
    public string Path;

    [Param("The deck as Markdown, one \"# Title\" per slide.")]
    public string Content;

    public override async Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".pptx", CallThread, out string? refusal);
        if (refusal is not null) return refusal;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string payload = System.Text.Json.JsonSerializer.Serialize(new { output = full, markdown = Content, folder = WorkingFolder });
        (int code, string output, string err) = await DocumentVenv.RunScript(PptxScript.Source, payload, WorkingFolder);
        if (code != 0) return $"[Error: couldn't build the deck: {(err.Length > 0 ? err.Trim() : output.Trim())}]";
        SaveRules.Created(CallThread, full);
        return $"Saved {full} ({output.Trim()}). Give it to the user with deliver_file." + AfterWriting.Check;
    }
}

internal static class PptxScript
{
    internal const string Source = """
import sys, json, re, os
from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.dml.color import RGBColor

cfg = json.load(sys.stdin)
prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
INK, ACCENT = RGBColor(0x10,0x16,0x18), RGBColor(0x2E,0x6F,0x80)

def runs(par, text, size, bold=False, color=INK):
    parts = re.split(r'(\*\*.+?\*\*|\*.+?\*)', text)
    for part in parts:
        if not part: continue
        r = par.add_run()
        if part.startswith('**') and part.endswith('**'): r.text, r.font.bold = part[2:-2], True
        elif part.startswith('*') and part.endswith('*') and len(part) > 2: r.text, r.font.italic = part[1:-1], True
        else: r.text, r.font.bold = part, bold
        r.font.size, r.font.color.rgb = Pt(size), color

slides, cur = [], None
for line in cfg["markdown"].replace('\r','').split('\n'):
    m = re.match(r'^#{1,2}\s+(.*)$', line)
    if m: cur = {"title": m.group(1).strip(), "lines": [], "notes": []}; slides.append(cur); continue
    if cur is None:
        if line.strip(): cur = {"title": line.strip(), "lines": [], "notes": []}; slides.append(cur)
        continue
    cur["lines"].append(line)

blank = prs.slide_layouts[6]
for i, s in enumerate(slides):
    slide = prs.slides.add_slide(blank)
    body, notes, in_notes = [], [], False
    for l in s["lines"]:
        if l.strip().lower() == 'notes:': in_notes = True; continue
        (notes if in_notes else body).append(l)
    body = [l for l in body]
    if i == 0:
        tb = slide.shapes.add_textbox(Inches(1), Inches(2.4), Inches(11.3), Inches(1.6)); tf = tb.text_frame; tf.word_wrap = True
        runs(tf.paragraphs[0], s["title"], 44, True, ACCENT)
        sub = [l.strip() for l in body if l.strip()]
        if sub:
            tb2 = slide.shapes.add_textbox(Inches(1), Inches(4.1), Inches(11.3), Inches(1.5)); t2 = tb2.text_frame; t2.word_wrap = True
            for j, l in enumerate(sub): runs(t2.paragraphs[0] if j == 0 else t2.add_paragraph(), l.lstrip('-* ').strip(), 22)
    else:
        bar = slide.shapes.add_shape(1, 0, 0, prs.slide_width, Inches(0.18)); bar.fill.solid(); bar.fill.fore_color.rgb = ACCENT; bar.line.fill.background()
        tb = slide.shapes.add_textbox(Inches(0.7), Inches(0.5), Inches(11.9), Inches(1.0)); tf = tb.text_frame; tf.word_wrap = True
        runs(tf.paragraphs[0], s["title"], 32, True, ACCENT)
        top, rows, images, items = Inches(1.6), [], [], []
        for l in body:
            if l.strip().startswith('|'):
                if re.match(r'^\s*\|?[\s:|-]+\|?\s*$', l) and '-' in l: continue
                rows.append([c.strip() for c in l.strip().strip('|').split('|')])
            elif re.match(r'^\s*!\[.*\]\(.+\)\s*$', l): images.append(re.match(r'^\s*!\[(.*)\]\((.+)\)', l).groups())
            elif l.strip(): items.append(l)
        if items:
            bx = slide.shapes.add_textbox(Inches(0.7), top, Inches(11.9) if not images else Inches(6.4), Inches(5.2)); bf = bx.text_frame; bf.word_wrap = True
            for j, l in enumerate(items):
                p = bf.paragraphs[0] if j == 0 else bf.add_paragraph()
                lvl = (len(l) - len(l.lstrip())) // 2; bullet = re.match(r'^\s*[-*•]\s+', l)
                text = re.sub(r'^\s*[-*•]\s+', '', l).strip()
                runs(p, ('•  ' if bullet else '') + text, 22 - 2 * min(lvl, 2)); p.level = min(lvl, 4); p.space_after = Pt(8)
            top += Inches(0.0)
        if rows:
            ncols = max(len(r) for r in rows); y = Inches(1.6) if not items else Inches(1.6 + min(len(items), 6) * 0.6)
            tbl = slide.shapes.add_table(len(rows), ncols, Inches(0.7), y, Inches(11.9), Inches(0.5) * len(rows)).table
            for ri, r in enumerate(rows):
                for ci in range(ncols):
                    cell = tbl.cell(ri, ci); cell.text = ''
                    runs(cell.text_frame.paragraphs[0], r[ci] if ci < len(r) else '', 16, ri == 0, RGBColor(255,255,255) if ri == 0 else INK)
                    if ri == 0: cell.fill.solid(); cell.fill.fore_color.rgb = ACCENT
        for cap, path in images:
            full = path if os.path.isabs(path) else os.path.join(cfg["folder"], path)
            if os.path.exists(full):
                slide.shapes.add_picture(full, Inches(7.4), Inches(1.6), width=Inches(5.3))
    if notes: slide.notes_slide.notes_text_frame.text = '\n'.join(n for n in notes if n.strip())
prs.save(cfg["output"])
print(f"{len(slides)} slide{'s' if len(slides) != 1 else ''}")
""";
}
