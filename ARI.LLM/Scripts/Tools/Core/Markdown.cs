using System.Text.RegularExpressions;

namespace ARI.LLM;

// Documents are written as a small, forgiving subset of Markdown, which the model produces reliably and the document tools turn into
// Word, PDF or slides. Supported: # headings (1-3), paragraphs, - or * bullets (indent two spaces to nest), 1. numbered lists, **bold**,
// *italic*, `code`, [text](url), | tables | with a --- separator row, a line of --- for a divider, and a line "\pagebreak" for a new page.
internal enum BlockKind { Heading, Paragraph, Bullet, Number, Table, Rule, PageBreak }

internal sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

internal sealed class Block
{
    internal BlockKind Kind;
    internal int Level;                                 // heading level 1-3, or list nesting depth
    internal List<Run> Runs = new();
    internal List<List<List<Run>>> Rows = new();        // tables: rows of cells of runs; the first row is the header
}

internal static class MarkdownDoc
{
    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|\[(.+?)\]\((.+?)\)", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(\s*)\d+[.)]\s+(.*)$", RegexOptions.Compiled);

    internal static List<Run> Runs(string text)
    {
        List<Run> runs = new(); int at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at) runs.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success) runs.Add(new Run(m.Groups[1].Value, Bold: true));
            else if (m.Groups[2].Success) runs.Add(new Run(m.Groups[2].Value, Italic: true));
            else if (m.Groups[3].Success) runs.Add(new Run(m.Groups[3].Value, Code: true));
            else runs.Add(new Run(m.Groups[4].Value, Link: m.Groups[5].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) runs.Add(new Run(text[at..]));
        return runs;
    }

    private static bool IsTableLine(string l) => l.TrimStart().StartsWith('|');
    private static bool IsSeparator(string l) => Regex.IsMatch(l, @"^\s*\|?[\s:|-]+\|?\s*$") && l.Contains('-');

    internal static List<Block> Parse(string markdown)
    {
        List<Block> blocks = new();
        string[] lines = markdown.Replace("\r", "").Split('\n');
        List<string> para = new();
        void Flush() { if (para.Count > 0) { blocks.Add(new Block { Kind = BlockKind.Paragraph, Runs = Runs(string.Join(" ", para)) }); para.Clear(); } }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i], t = line.Trim();
            if (t.Length == 0) { Flush(); continue; }
            if (t is "\\pagebreak" or "<pagebreak>" or "---pagebreak---") { Flush(); blocks.Add(new Block { Kind = BlockKind.PageBreak }); continue; }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) { Flush(); blocks.Add(new Block { Kind = BlockKind.Rule }); continue; }
            Match h = Regex.Match(t, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (h.Success) { Flush(); blocks.Add(new Block { Kind = BlockKind.Heading, Level = Math.Min(3, h.Groups[1].Length), Runs = Runs(h.Groups[2].Value) }); continue; }
            if (IsTableLine(line))
            {
                Flush();
                Block table = new() { Kind = BlockKind.Table };
                for (; i < lines.Length && IsTableLine(lines[i]); i++)
                {
                    if (IsSeparator(lines[i])) continue;
                    string row = lines[i].Trim().Trim('|');
                    table.Rows.Add(row.Split('|').Select(c => Runs(c.Trim())).ToList());
                }
                i--; blocks.Add(table); continue;
            }
            Match b = Bullet.Match(line), n = Numbered.Match(line);
            if (b.Success || n.Success)
            {
                Flush(); Match m = b.Success ? b : n;
                blocks.Add(new Block { Kind = b.Success ? BlockKind.Bullet : BlockKind.Number, Level = Math.Min(2, m.Groups[1].Length / 2), Runs = Runs(m.Groups[2].Value) });
                continue;
            }
            para.Add(t);
        }
        Flush();
        return blocks;
    }

    internal static string PlainText(IEnumerable<Run> runs) => string.Concat(runs.Select(r => r.Text));

    internal static string Html(string markdown, string title)
    {
        System.Text.StringBuilder sb = new();
        string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        string R(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
        {
            string s = Esc(r.Text);
            if (r.Code) s = $"<code>{s}</code>"; if (r.Bold) s = $"<strong>{s}</strong>"; if (r.Italic) s = $"<em>{s}</em>";
            return r.Link is { } l ? $"<a href=\"{Esc(l)}\">{s}</a>" : s;
        }));
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(Esc(title)).Append("</title><style>")
          .Append("body{font-family:-apple-system,'Helvetica Neue',Arial,sans-serif;font-size:11pt;line-height:1.45;color:#1a1a1a;margin:0}")
          .Append("h1{font-size:22pt;margin:0 0 6pt}h2{font-size:15pt;margin:16pt 0 4pt;border-bottom:1px solid #ccc;padding-bottom:2pt}h3{font-size:12pt;margin:12pt 0 2pt}")
          .Append("p{margin:4pt 0}ul,ol{margin:4pt 0 4pt 18pt;padding:0}table{border-collapse:collapse;margin:8pt 0;width:100%}")
          .Append("th,td{border:1px solid #bbb;padding:4pt 6pt;text-align:left;font-size:10pt}th{background:#eef2f4}hr{border:0;border-top:1px solid #ccc;margin:10pt 0}")
          .Append("code{font-family:Menlo,monospace;background:#f2f2f2;padding:0 2pt}.pb{page-break-after:always}</style></head><body>");
        string? open = null;
        void Close() { if (open is not null) { sb.Append($"</{open}>"); open = null; } }
        foreach (Block b in Parse(markdown))
        {
            if (b.Kind is not (BlockKind.Bullet or BlockKind.Number)) Close();
            switch (b.Kind)
            {
                case BlockKind.Heading: sb.Append($"<h{b.Level}>{R(b.Runs)}</h{b.Level}>"); break;
                case BlockKind.Paragraph: sb.Append($"<p>{R(b.Runs)}</p>"); break;
                case BlockKind.Bullet or BlockKind.Number:
                    string tag = b.Kind == BlockKind.Bullet ? "ul" : "ol";
                    if (open != tag) { Close(); sb.Append($"<{tag}>"); open = tag; }
                    sb.Append($"<li>{R(b.Runs)}</li>"); break;
                case BlockKind.Table:
                    sb.Append("<table>");
                    for (int r = 0; r < b.Rows.Count; r++)
                        sb.Append("<tr>").Append(string.Concat(b.Rows[r].Select(c => r == 0 ? $"<th>{R(c)}</th>" : $"<td>{R(c)}</td>"))).Append("</tr>");
                    sb.Append("</table>"); break;
                case BlockKind.Rule: sb.Append("<hr>"); break;
                case BlockKind.PageBreak: sb.Append("<div class=\"pb\"></div>"); break;
            }
        }
        Close();
        return sb.Append("</body></html>").ToString();
    }
}
