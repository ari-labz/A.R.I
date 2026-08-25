using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>A Jupyter notebook read is a type of read. A raw .ipynb is JSON the model shouldn't wade
/// through, so this flattens it to the cell sources in order. A malformed notebook falls back to
/// base.Decode — plain text.</summary>
internal sealed class ReadNotebook : Read
{
    internal ReadNotebook(FileSystem fs) : base(fs) { }

    protected override ToolResult Decode(byte[] raw, string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            StringBuilder sb = new();
            int cellNumber = 0;
            foreach (JsonElement cell in doc.RootElement.GetProperty("cells").EnumerateArray())
            {
                cellNumber++;
                string kind = cell.TryGetProperty("cell_type", out JsonElement t) ? t.GetString() ?? "" : "";
                sb.AppendLine($"# ── cell {cellNumber} ({kind}) ──");
                sb.AppendLine(Source(cell));
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch { return base.Decode(raw, path); }
    }

    /// <summary>A cell's "source" is a JSON string or an array of line strings; join either into one block.</summary>
    private static string Source(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out JsonElement src)) return "";
        if (src.ValueKind == JsonValueKind.String) return src.GetString() ?? "";
        if (src.ValueKind != JsonValueKind.Array) return "";

        StringBuilder sb = new();
        foreach (JsonElement line in src.EnumerateArray())
            sb.Append(line.GetString());
        return sb.ToString();
    }
}
