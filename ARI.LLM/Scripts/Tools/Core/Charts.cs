namespace ARI.LLM;

[Tool("make_chart", "Draw a chart as a PNG picture: bar, horizontal bar, line, area, pie, scatter or histogram. Give the data as CSV text: a header row, then one row per category (or x value); " +
                    "the first column is the labels (or x) and every other column is one series. Put the picture in a Word document, PDF or deck with ![caption](chart.png), " +
                    "or give it to the user with deliver_file. Use only numbers from the user's data or that you calculated. The first use sets up a small Python environment, which takes a minute.", Group = "chart_tools")]
public class MakeChart : Tool
{
    [Param("Where to save it, ending in .png, e.g. revenue.png")]
    public string Path;

    [Param("bar, barh, line, area, pie, scatter or hist.")]
    public string Type;

    [Param("The data as CSV: a header row, then rows. First column = labels or x values; other columns = series.")]
    public string Data;

    [Param("The chart's title.", Optional = true)]
    public string Title;

    [Param("Label for the x axis.", Optional = true)]
    public string XLabel;

    [Param("Label for the y axis.", Optional = true)]
    public string YLabel;

    [Param("true to stack the series in a bar or area chart. Default false.", Optional = true)]
    public bool Stacked = false;

    public override async Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".png", CallThread, out string? refusal);
        if (refusal is not null) return refusal;
        string kind = (Type ?? "").Trim().ToLowerInvariant();
        if (kind is not ("bar" or "barh" or "line" or "area" or "pie" or "scatter" or "hist")) return "[Error: type must be bar, barh, line, area, pie, scatter or hist.]";
        List<List<string>> rows = Data.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(Xlsx.SplitCsv).ToList();
        if (rows.Count < 2 || rows[0].Count < 2 && kind != "hist") return "[Error: give a header row and at least one data row, with a labels column and at least one number column.]";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        await DocumentVenv.EnsurePackage("matplotlib");
        string payload = System.Text.Json.JsonSerializer.Serialize(new { output = full, type = kind, rows, title = Title ?? "", xlabel = XLabel ?? "", ylabel = YLabel ?? "", stacked = Stacked });
        (int code, string output, string err) = await DocumentVenv.RunScript(Source, payload, WorkingFolder, "chart_build.py");
        if (code != 0) return $"[Error: couldn't draw the chart: {(err.Length > 0 ? err.Trim().Split('\n').Last() : output.Trim())}]";
        SaveRules.Created(CallThread, full);
        return $"Saved {full} ({output.Trim()}). Put it in a document with ![caption]({System.IO.Path.GetFileName(full)}) or give it to the user with deliver_file.";
    }

    private const string Source = """
import sys, json
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

cfg = json.load(sys.stdin)
rows = cfg["rows"]; head = rows[0]; body = rows[1:]
PALETTE = ["#2E6F80", "#E07A5F", "#81B29A", "#F2CC8F", "#3D405B", "#9C6644", "#6D597A", "#B56576"]

def num(v):
    try: return float(str(v).replace(",", "").replace("%", "").replace("$", "").replace("£", ""))
    except ValueError: return None

labels = [r[0] for r in body]
series = [(head[c] if c < len(head) else f"Series {c}", [num(r[c]) if c < len(r) else None for r in body]) for c in range(1, len(head))]
fig, ax = plt.subplots(figsize=(8, 4.8), dpi=150)
t = cfg["type"]
if t == "hist":
    vals = [num(r[0]) for r in body if num(r[0]) is not None] if len(head) < 2 else [v for v in series[0][1] if v is not None]
    ax.hist(vals, bins=min(20, max(5, len(vals) // 3)), color=PALETTE[0], edgecolor="white")
elif t == "pie":
    vals = [v or 0 for v in series[0][1]]
    ax.pie(vals, labels=labels, colors=PALETTE, autopct="%1.0f%%", startangle=90, wedgeprops={"edgecolor": "white"}); ax.axis("equal")
elif t in ("bar", "barh"):
    n = len(series); w = 0.8 / n; xs = range(len(labels)); bottom = [0] * len(labels)
    for i, (name, vals) in enumerate(series):
        vals = [v or 0 for v in vals]
        pos = [x if cfg["stacked"] else x + (i - (n - 1) / 2) * w for x in xs]
        if t == "bar": ax.bar(pos, vals, width=0.8 if cfg["stacked"] else w, label=name, color=PALETTE[i % 8], bottom=bottom if cfg["stacked"] else None)
        else: ax.barh(pos, vals, height=0.8 if cfg["stacked"] else w, label=name, color=PALETTE[i % 8], left=bottom if cfg["stacked"] else None)
        if cfg["stacked"]: bottom = [b + v for b, v in zip(bottom, vals)]
    (ax.set_xticks if t == "bar" else ax.set_yticks)(list(xs)); (ax.set_xticklabels if t == "bar" else ax.set_yticklabels)(labels, rotation=30 if t == "bar" and len(labels) > 6 else 0, ha="right" if t == "bar" and len(labels) > 6 else "center")
elif t == "scatter":
    for i, (name, vals) in enumerate(series):
        xs = [num(l) for l in labels]
        pts = [(x, y) for x, y in zip(xs, vals) if x is not None and y is not None]
        ax.scatter([p[0] for p in pts], [p[1] for p in pts], label=name, color=PALETTE[i % 8], s=28)
else:
    xs = list(range(len(labels))) if any(num(l) is None for l in labels) else [num(l) for l in labels]
    if t == "area":
        ax.stackplot(xs, *[[v or 0 for v in vals] for _, vals in series], labels=[n for n, _ in series], colors=PALETTE) if cfg["stacked"] else [ax.fill_between(xs, [v or 0 for v in vals], alpha=0.35, color=PALETTE[i % 8], label=n) for i, (n, vals) in enumerate(series)]
    else:
        for i, (name, vals) in enumerate(series): ax.plot(xs, vals, marker="o", markersize=4, linewidth=2, label=name, color=PALETTE[i % 8])
    if any(num(l) is None for l in labels): ax.set_xticks(xs); ax.set_xticklabels(labels, rotation=30 if len(labels) > 6 else 0, ha="right" if len(labels) > 6 else "center")
if t != "pie":
    ax.spines[["top", "right"]].set_visible(False); ax.grid(axis="y" if t != "barh" else "x", alpha=0.25)
    if cfg["xlabel"]: ax.set_xlabel(cfg["xlabel"])
    if cfg["ylabel"]: ax.set_ylabel(cfg["ylabel"])
    if len(series) > 1 and t != "hist": ax.legend(frameon=False)
if cfg["title"]: ax.set_title(cfg["title"], fontsize=13, fontweight="bold", color="#101618")
fig.tight_layout(); fig.savefig(cfg["output"], facecolor="white"); print(f"{t} chart, {len(body)} rows")
""";
}
