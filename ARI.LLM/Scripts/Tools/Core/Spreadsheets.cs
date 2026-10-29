using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace ARI.LLM;

// Making and editing Excel files. Ari can only write text, so a workbook is described as text: sheets, then rows of comma-separated
// values, where a value starting with = is a formula. Files land in the conversation's working folder; hand one over with deliver_file.
internal static class Xlsx
{
    internal static string Resolve(string workingFolder, string path)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingFolder, path));
        if (!full.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The file name must end in .xlsx.");
        return full;
    }

    /// <summary>One comma-separated line, with "quoted, values" and "" for a quote inside one.</summary>
    internal static List<string> SplitCsv(string line)
    {
        List<string> cells = new(); StringBuilder cur = new(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else if (c == '"') quoted = false; else cur.Append(c); }
            else if (c == '"' && cur.Length == 0) quoted = true;
            else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    /// <summary>Puts a value in a cell as the kind it looks like: formula, number, true/false, date, or text.</summary>
    internal static void SetValue(IXLCell cell, string raw)
    {
        string v = raw.Trim();
        if (v.Length == 0) { cell.Clear(); return; }
        if (v.StartsWith('=') && v.Length > 1) cell.FormulaA1 = v[1..];
        else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !v.StartsWith('0') || v == "0" || (v.StartsWith("0.") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)))
            cell.Value = double.Parse(v, CultureInfo.InvariantCulture);
        else if (bool.TryParse(v, out bool b)) cell.Value = b;
        else if (v.Length >= 8 && DateTime.TryParseExact(v, new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)) { cell.Value = dt; cell.Style.DateFormat.Format = v.Length == 10 ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm"; }
        else cell.Value = raw;
    }
}

[Tool("write_xlsx", "Create an Excel workbook (.xlsx) from text. Give each sheet a line starting with ## and its name, then its rows as comma-separated values, one row per line. " +
                    "A value starting with = is a formula (=SUM(B2:B9)); numbers, true/false and dates like 2026-10-29 are stored as such. Quote a value that contains a comma. " +
                    "It saves into your working folder; give it to the user with deliver_file. It never overwrites a file that was there before the conversation. To change an existing workbook use edit_xlsx.", Group = "xlsx_tools")]
public class WriteXlsx : Tool
{
    [Param("Where to save it, ending in .xlsx, e.g. budget.xlsx")]
    public string Path;

    [Param("The workbook as text: \"## Sheet name\" lines, then CSV rows. With no ## line everything goes in one sheet.")]
    public string Content;

    [Param("true to make the first row of each sheet bold with a filter. Default true.", Optional = true)]
    public bool Header = true;

    public override Task<string> Run()
    {
        string full = SaveRules.Resolve(WorkingFolder, Path, ".xlsx", CallThread, out string? refusal);
        if (refusal is not null) return Task.FromResult(refusal);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using XLWorkbook wb = new();
        IXLWorksheet? ws = null; int row = 0, sheets = 0, cells = 0;
        foreach (string line in Content.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("##")) { ws = wb.AddWorksheet(line.TrimStart('#').Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 31)] : $"Sheet{sheets + 1}"); row = 0; sheets++; continue; }
            if (line.Trim().Length == 0 && ws is null) continue;
            if (ws is null) { ws = wb.AddWorksheet("Sheet1"); sheets++; }
            row++;
            List<string> values = Xlsx.SplitCsv(line);
            for (int c = 0; c < values.Count; c++) { Xlsx.SetValue(ws.Cell(row, c + 1), values[c]); cells++; }
        }
        foreach (IXLWorksheet s in wb.Worksheets)
        {
            if (Header && s.RangeUsed() is { } used) { used.FirstRow().Style.Font.Bold = true; used.SetAutoFilter(); }
            s.Columns().AdjustToContents(1, 60);
        }
        if (sheets == 0) return Task.FromResult("[Error: there was nothing to write.]");
        wb.SaveAs(full);
        SaveRules.Created(CallThread, full);
        return Task.FromResult($"Saved {full} ({sheets} sheet{(sheets == 1 ? "" : "s")}, {cells} cells). Give it to the user with deliver_file.");
    }
}

[Tool("edit_xlsx", "Change an existing Excel workbook: set cells, add rows, add sheets. One edit per line. " +
                   "Set a cell:  Sheet1!B2 = 42   (the sheet name is optional and defaults to the first). A value starting with = is a formula:  C10 = =SUM(C2:C9). " +
                   "Add a row after the last used row:  append Sheet1: Rent,1200,=B5*12. Add a sheet:  sheet Totals. Look at the file first with read_xlsx so you know the layout. The user's own workbook is never changed in place: your edits go to a copy.", Group = "xlsx_tools")]
public class EditXlsx : Tool
{
    [Param("The workbook to change, ending in .xlsx.")]
    public string Path;

    [Param("The edits, one per line, in the formats above.")]
    public string Edits;

    [Param("Where to save the changed copy, ending in .xlsx. The user's own workbook is never changed in place, so this defaults to \"<name> - edited.xlsx\".", Optional = true)]
    public string SaveAs;

    public override Task<string> Run()
    {
        string full = Xlsx.Resolve(WorkingFolder, Path);
        if (!File.Exists(full)) return Task.FromResult($"[Error: {full} doesn't exist. Create it with write_xlsx.]");
        // The user's own workbook is never changed in place: the edits go to a copy.
        if (CallThread is not null && !CallThread.CreatedFiles.Contains(full))
        {
            string name = string.IsNullOrWhiteSpace(SaveAs) ? System.IO.Path.GetFileNameWithoutExtension(full) + " - edited.xlsx" : SaveAs;
            string copy = SaveRules.Resolve(WorkingFolder, name, ".xlsx", CallThread, out string? refusal);
            if (refusal is not null) return Task.FromResult(refusal);
            File.Copy(full, copy, overwrite: true); full = copy;
        }
        SaveRules.Created(CallThread, full);
        using XLWorkbook wb = new(full);
        int done = 0; List<string> problems = new();
        foreach (string raw in Edits.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                if (line.StartsWith("sheet ", StringComparison.OrdinalIgnoreCase)) { wb.AddWorksheet(line[6..].Trim()); done++; continue; }
                if (line.StartsWith("append ", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    string name = line[7..colon].Trim();
                    IXLWorksheet ws = wb.TryGetWorksheet(name, out IXLWorksheet? w) ? w : wb.AddWorksheet(name);
                    int next = (ws.LastRowUsed()?.RowNumber() ?? 0) + 1;
                    List<string> values = Xlsx.SplitCsv(line[(colon + 1)..].TrimStart());
                    for (int c = 0; c < values.Count; c++) Xlsx.SetValue(ws.Cell(next, c + 1), values[c]);
                    done++; continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { problems.Add($"\"{line}\": expected  CELL = value"); continue; }
                string target = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                string sheetName = wb.Worksheets.First().Name, cell = target;
                if (target.Contains('!')) { sheetName = target[..target.LastIndexOf('!')].Trim('\'', ' '); cell = target[(target.LastIndexOf('!') + 1)..].Trim(); }
                if (!wb.TryGetWorksheet(sheetName, out IXLWorksheet? sheet)) { problems.Add($"\"{line}\": no sheet called {sheetName}"); continue; }
                Xlsx.SetValue(sheet.Cell(cell), value);
                done++;
            }
            catch (Exception e) { problems.Add($"\"{line}\": {e.Message}"); }
        }
        wb.Save();
        return Task.FromResult($"Applied {done} edit{(done == 1 ? "" : "s")} to {full}." + (problems.Count > 0 ? $"\nCouldn't apply:\n- {string.Join("\n- ", problems)}" : ""));
    }
}
