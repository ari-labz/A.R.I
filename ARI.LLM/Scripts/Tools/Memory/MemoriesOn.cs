using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.BrainVault;

namespace ARI.LLM;

// "What was I doing on the 20th of February 2025?" — everything the brain holds for one day: that day's conversation log, the notes
// written that day, and notes that name the date.
internal sealed class MemoriesOn : Tool
{
    private static readonly Regex created = new(@"^created: (\d{4}-\d{2}-\d{2})", RegexOptions.Multiline | RegexOptions.Compiled);

    internal override string     Name   => "memories_on";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "memories_on",
            description = "Everything the brain holds for one calendar day: that day's conversation log, the notes written that day, and notes that mention the date. Use it for questions about a particular day (\"what was I doing on 20 Feb 2025?\").",
            parameters  = new
            {
                type       = "object",
                properties = new { date = new { type = "string", description = "The day, as YYYY-MM-DD." } },
                required   = new[] { "date" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string raw = "";
        try { raw = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.TryGetProperty("date", out JsonElement d) ? d.GetString() ?? "" : ""; } catch { }
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            return Task.FromResult<ToolResult>("Error: 'date' must be a day like 2025-02-20.");
        Brain.Index();

        StringBuilder sb = new();
        sb.AppendLine($"{day:dddd d MMMM yyyy}");

        if (Brain.GetNote(Brain.DailyNoteName(day)) is { } log)
            sb.AppendLine("\nCONVERSATION LOG:\n" + log.Content.Trim());

        string iso = day.ToString("yyyy-MM-dd");
        List<string> written = new();
        foreach (string title in Brain.GetTitles())
        {
            if (Brain.GetNote(title) is not { } note) continue;
            string file = Path.Combine(Brain.VaultRoot, note.Path.EndsWith(".md") ? note.Path : note.Path + ".md");
            if (!File.Exists(file)) continue;
            if (created.Match(File.ReadAllText(file)) is { Success: true } m && m.Groups[1].Value == iso) written.Add(note.Name);
        }
        if (written.Count > 0) sb.AppendLine("\nNOTES WRITTEN THAT DAY:\n" + string.Join("\n", written.Select(n => "- " + n)));

        int dd = day.Day;
        string suffix = dd is >= 11 and <= 13 ? "th" : (dd % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        string[] terms = [iso, $"{dd}{suffix} {day:MMMM yyyy}", $"{dd} {day:MMM yyyy}", $"{dd} {day:MMMM yyyy}"];
        List<string> mentioning = terms.SelectMany(t => Brain.Search([t], 6)).Select(r => r.Note.Name).Distinct().Except(written).Take(8).ToList();
        if (mentioning.Count > 0) sb.AppendLine("\nNOTES THAT MENTION THE DATE:\n" + string.Join("\n", mentioning.Select(n => "- " + n)));

        if (written.Count == 0 && mentioning.Count == 0 && sb.ToString().Count(c => c == '\n') <= 1)
            sb.AppendLine("\nNothing is stored for that day.");
        return Task.FromResult<ToolResult>(sb.ToString().TrimEnd());
    }
}
