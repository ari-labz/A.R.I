using System.Text;
using System.Web;

namespace ARI.Brain;

public static class NoteBuilder
{
    // Placeholder format resolved later once note IDs are known
    public const string LinkPlaceholder = "{{LINK:{0}}}";

    public static string BuildOrMergePerson(ExtractedNote incoming, string? existing)
    {
        NoteData note = existing is not null ? NoteData.Parse(existing) : new NoteData();

        note.Name = incoming.Name;

        if (incoming.Pronouns is not null && (note.Pronouns is null or "Unknown"))
            note.Pronouns = incoming.Pronouns;

        if (incoming.Relation is not null && (note.Relation is null or "Unknown"))
            note.Relation = incoming.Relation;

        MergeList(note.Aliases, incoming.Aliases);
        MergeList(note.Info, incoming.Info);
        MergeList(note.Feelings, incoming.Feelings);
        MergeList(note.Observations, incoming.Observations);

        string today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("dd/MM/yyyy");
        foreach (string ev in incoming.Events)
        {
            string entry = $"{today}: {ev}";
            if (!note.Events.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                note.Events.Add(entry);
                note.ChangeLog.Add($"{today}: Added event — {ev}");
            }
        }

        return note.ToHtml();
    }

    public static string BuildOrMergeEvent(ExtractedNote incoming, string? existing)
    {
        NoteData note = existing is not null ? NoteData.Parse(existing) : new NoteData();

        if (string.IsNullOrWhiteSpace(note.Name)) note.Name = incoming.Name;
        if (string.IsNullOrWhiteSpace(note.Date))
            note.Date = incoming.Date ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("dd/MM/yyyy");

        string today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("dd/MM/yyyy");
        foreach (string info in incoming.Info)
        {
            if (!note.Info.Contains(info, StringComparer.OrdinalIgnoreCase))
            {
                note.Info.Add(info);
                note.ChangeLog.Add($"{today}: Added — {info}");
            }
        }

        MergeList(note.Feelings, incoming.Feelings);
        MergeList(note.Observations, incoming.Observations);
        return note.ToHtml();
    }

    public static string BuildOrMergeGeneric(ExtractedNote incoming, string? existing)
    {
        NoteData note = existing is not null ? NoteData.Parse(existing) : new NoteData();

        note.Name = incoming.Name;
        MergeList(note.Info, incoming.Info);
        MergeList(note.Feelings, incoming.Feelings);
        MergeList(note.Observations, incoming.Observations);
        MergeList(note.Aliases, incoming.Aliases);

        return note.ToHtml();
    }

    // Replaces {{LINK:NoteName}} placeholders with Trilium HTML anchor tags.
    // Only replaces in the visible body — skips the metadata div to avoid corrupting data-* attributes.
    public static string ResolveLinks(string html, Dictionary<string, string> noteIds)
    {
        // The metadata div is always first; stop at its closing tag
        int bodyStart = html.IndexOf("</div>", StringComparison.OrdinalIgnoreCase);
        string meta = bodyStart >= 0 ? html[..(bodyStart + 6)] : string.Empty;
        string body = bodyStart >= 0 ? html[(bodyStart + 6)..] : html;

        foreach (var (name, id) in noteIds)
        {
            string placeholder = string.Format(LinkPlaceholder, name);
            string link = $"<a class=\"reference-link\" data-note-path=\"root/{id}\">{HttpUtility.HtmlEncode(name)}</a>";
            body = body.Replace(placeholder, link, StringComparison.OrdinalIgnoreCase);
        }

        return meta + body;
    }

    private static void MergeList(List<string> target, IEnumerable<string> incoming)
    {
        foreach (string item in incoming)
        {
            if (!string.IsNullOrWhiteSpace(item) &&
                !target.Any(e => string.Equals(e.Trim(), item.Trim(), StringComparison.OrdinalIgnoreCase)))
                target.Add(item);
        }
    }

    // ── Internal note data model ────────────────────────────────────────────────

    internal class NoteData
    {
        public string Name { get; set; } = string.Empty;
        public string? Pronouns { get; set; }
        public string? Relation { get; set; }
        public string? Date { get; set; }
        public List<string> Aliases { get; set; } = new();
        public List<string> Info { get; set; } = new();
        public List<string> Feelings { get; set; } = new();
        public List<string> Observations { get; set; } = new();
        public List<string> Events { get; set; } = new();
        public List<string> ChangeLog { get; set; } = new();

        // Parses the data-* attributes embedded in the HTML so we can merge
        public static NoteData Parse(string html)
        {
            NoteData note = new();

            note.Name         = ExtractMeta(html, "name");
            note.Pronouns     = ExtractMeta(html, "pronouns");
            note.Relation     = ExtractMeta(html, "relation");
            note.Date         = ExtractMeta(html, "date");
            note.Aliases      = ExtractList(html, "aliases");
            note.Info         = ExtractList(html, "info");
            note.Feelings     = ExtractList(html, "feelings");
            note.Observations = ExtractList(html, "observations");
            note.Events       = ExtractList(html, "events");
            note.ChangeLog    = ExtractList(html, "changelog");

            return note;
        }

        public string ToHtml()
        {
            var sb = new StringBuilder();

            // Hidden metadata div for round-trip parsing
            sb.AppendLine($"<div data-ari-note=\"true\" data-name=\"{Enc(Name)}\" data-pronouns=\"{Enc(Pronouns)}\" data-relation=\"{Enc(Relation)}\" data-date=\"{Enc(Date)}\"");
            sb.AppendLine($"  data-aliases=\"{EncList(Aliases)}\" data-info=\"{EncList(Info)}\" data-feelings=\"{EncList(Feelings)}\" data-observations=\"{EncList(Observations)}\" data-events=\"{EncList(Events)}\" data-changelog=\"{EncList(ChangeLog)}\"></div>");

            // Metadata pills
            if (!string.IsNullOrWhiteSpace(Pronouns) && Pronouns != "Unknown")
                sb.AppendLine($"<p><strong>Pronouns:</strong> {Enc(Pronouns)}</p>");

            if (!string.IsNullOrWhiteSpace(Relation) && Relation != "Unknown")
                sb.AppendLine($"<p><strong>Relation:</strong> {Enc(Relation)}</p>");

            if (!string.IsNullOrWhiteSpace(Date))
                sb.AppendLine($"<p><strong>Date:</strong> {Enc(Date)}</p>");

            if (Aliases.Count > 0)
            {
                sb.AppendLine("<p><strong>Also known as:</strong></p><ul>");
                foreach (string a in Aliases) sb.AppendLine($"<li>{Enc(a)}</li>");
                sb.AppendLine("</ul>");
            }

            if (Info.Count > 0)
            {
                sb.AppendLine("<ul>");
                foreach (string i in Info) sb.AppendLine($"<li>{i}</li>");
                sb.AppendLine("</ul>");
            }

            if (Events.Count > 0)
            {
                sb.AppendLine("<p><strong>Events:</strong></p><ul>");
                foreach (string e in Events) sb.AppendLine($"<li>{Enc(e)}</li>");
                sb.AppendLine("</ul>");
            }

            if (Feelings.Count > 0)
            {
                sb.AppendLine("<p><strong>[REDACT]'s feelings:</strong></p><ul>");
                foreach (string f in Feelings) sb.AppendLine($"<li>{f}</li>");
                sb.AppendLine("</ul>");
            }

            if (Observations.Count > 0)
            {
                sb.AppendLine("<p><strong>Ari's observations:</strong></p><ul>");
                foreach (string o in Observations) sb.AppendLine($"<li>{o}</li>");
                sb.AppendLine("</ul>");
            }

            if (ChangeLog.Count > 0)
            {
                sb.AppendLine("<p><strong>Changelog:</strong></p><ul>");
                foreach (string c in ChangeLog) sb.AppendLine($"<li>{Enc(c)}</li>");
                sb.AppendLine("</ul>");
            }

            return sb.ToString();
        }

        private static string Enc(string? s) => HttpUtility.HtmlEncode(s ?? string.Empty);

        private static string EncList(List<string> list) =>
            HttpUtility.HtmlEncode(string.Join("|||", list));

        private static string ExtractMeta(string html, string attr)
        {
            string marker = $"data-{attr}=\"";
            int start = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += marker.Length;
            int end = html.IndexOf('"', start);
            if (end < 0) return string.Empty;
            return HttpUtility.HtmlDecode(html[start..end]);
        }

        private static List<string> ExtractList(string html, string attr)
        {
            string raw = ExtractMeta(html, attr);
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split("|||", StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
}
