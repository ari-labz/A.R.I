using System.Text;

namespace ARI.LLM;

/// <summary>tool_search: find tools by what they do and load them in the same call. The way to discover a tool without
/// reading the whole list; list_tools stays as the overview. Matches the words of the query against each tool's name, group
/// and description; "select:a,b" loads exactly those.</summary>
internal sealed class ToolSearch : Tool
{
    private readonly Thread thread;
    internal ToolSearch(Thread thread) => this.thread = thread;

    internal override string     Name   => "tool_search";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "tool_search",
            description = "Find tools by what you want to do and load them so you can call them. Describe the job in a few words, " +
                          "e.g. \"read a pdf\", \"run python\", \"send an http request\", \"screenshot a page\". " +
                          "Use \"select:name1,name2\" to load tools whose names you already know. Do this before telling the user you can't do something.",
            parameters  = new
            {
                type       = "object",
                properties = new { query = new { type = "string", description = "What you want to do, or select:name1,name2." } },
                required   = new[] { "query" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        string query = "";
        try { query = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("query").GetString() ?? ""; } catch { }
        query = query.Trim();
        if (query.Length == 0) return Task.FromResult<ToolResult>("Error: say what you want to do.");

        List<string> names;
        if (query.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
            names = query[7..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        else
        {
            string[] plain = query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
            string[] words = Expand(query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));
            names = ToolFactories.AllNames()
                .Select(n => (Name: n, Score: Score(n, words, plain)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Name).Take(12).ToList();
        }

        StringBuilder sb = new();
        int loaded = 0;
        foreach (string name in names)
        {
            if (thread.tools.ContainsKey(name)) { sb.AppendLine($"  • {name} — already loaded"); loaded++; continue; }
            if (!ToolFactories.TryBuild(name, thread, out Tool tool)) continue;
            tool.Register(thread);
            sb.AppendLine($"  • {tool.Name} — {tool.SchemaDescription}");
            if (++loaded >= 6) break;
        }
        return Task.FromResult<ToolResult>(loaded == 0
            ? $"No tool matched \"{query}\". Here is everything you can load:\n{CapabilitiesBlock.Overview(thread)}\n" +
              "If one of these fits, load it with request_tools. If none does, and code you run, a command, or a tool you write can't do it either, tell the user plainly that you can't, what is missing, and what you can do instead."
            : $"Loaded:\n{sb.ToString().TrimEnd()}");
    }

    // What people say, mapped to what the tools are called, so "make a spreadsheet" finds write_xlsx and "what does this scan say" finds ocr.
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["spreadsheet"] = new[] { "xlsx", "excel" }, ["excel"] = new[] { "xlsx" }, ["workbook"] = new[] { "xlsx" }, ["csv"] = new[] { "xlsx", "convert" }, ["sheet"] = new[] { "xlsx" },
        ["word"] = new[] { "docx" }, ["resume"] = new[] { "docx" }, ["letter"] = new[] { "docx" }, ["report"] = new[] { "docx", "pdf" },
        ["slide"] = new[] { "pptx" }, ["slides"] = new[] { "pptx" }, ["deck"] = new[] { "pptx" }, ["powerpoint"] = new[] { "pptx" }, ["presentation"] = new[] { "pptx" },
        ["scan"] = new[] { "ocr" }, ["scanned"] = new[] { "ocr" }, ["handwriting"] = new[] { "ocr" }, ["recognise"] = new[] { "ocr" }, ["recognize"] = new[] { "ocr" }, ["receipt"] = new[] { "ocr" },
        ["picture"] = new[] { "image" }, ["photo"] = new[] { "image", "ocr" }, ["screenshot"] = new[] { "image", "ocr", "browser" }, ["png"] = new[] { "image" }, ["jpg"] = new[] { "image" },
        ["resize"] = new[] { "edit_image" }, ["crop"] = new[] { "edit_image" }, ["rotate"] = new[] { "edit_image" }, ["watermark"] = new[] { "edit_image" }, ["draw"] = new[] { "generate_image", "chart" },
        ["convert"] = new[] { "convert_document" }, ["conversion"] = new[] { "convert_document" }, ["markdown"] = new[] { "convert_document" }, ["html"] = new[] { "convert_document" },
        ["zip"] = new[] { "archive" }, ["unzip"] = new[] { "archive" }, ["compress"] = new[] { "archive" }, ["extract"] = new[] { "archive" },
        ["graph"] = new[] { "chart" }, ["plot"] = new[] { "chart" }, ["diagram"] = new[] { "visual" }, ["dashboard"] = new[] { "visual" },
        ["download"] = new[] { "download_file" }, ["website"] = new[] { "browser", "fetch_page" }, ["webpage"] = new[] { "browser", "fetch_page" }, ["url"] = new[] { "browser", "fetch_page", "http_request" },
        ["login"] = new[] { "browser" }, ["click"] = new[] { "browser" }, ["browse"] = new[] { "browser" }, ["api"] = new[] { "http_request" }, ["webhook"] = new[] { "http_request" },
        ["remote"] = new[] { "ssh" }, ["server"] = new[] { "ssh" }, ["machine"] = new[] { "ssh" }, ["linux"] = new[] { "ssh" },
        ["python"] = new[] { "run_code" }, ["script"] = new[] { "run_code" }, ["calculate"] = new[] { "run_code" }, ["compute"] = new[] { "run_code" },
        ["terminal"] = new[] { "shell" }, ["command"] = new[] { "shell" }, ["bash"] = new[] { "shell" }, ["install"] = new[] { "shell" },
        ["remember"] = new[] { "memory" }, ["recall"] = new[] { "memory" }, ["reminder"] = new[] { "calendar" }, ["schedule"] = new[] { "calendar" }, ["event"] = new[] { "calendar" },
        ["pull"] = new[] { "github" }, ["issue"] = new[] { "github" }, ["repo"] = new[] { "git" }, ["commit"] = new[] { "git" }, ["voice"] = new[] { "discord" },
        ["background"] = new[] { "jobs" }, ["cancel"] = new[] { "jobs" }, ["playbook"] = new[] { "skill" },
    };

    /// <summary>The words of the request plus what they mean in tool names, with plurals folded ("spreadsheets" is "spreadsheet").</summary>
    private static string[] Expand(IEnumerable<string> words)
    {
        HashSet<string> all = new();
        foreach (string raw in words)
        {
            string w = raw.Length > 4 && raw.EndsWith('s') && !raw.EndsWith("ss") ? raw[..^1] : raw;
            all.Add(w);
            if (Synonyms.TryGetValue(w, out string[]? extra)) foreach (string e in extra) all.Add(e);
        }
        return all.ToArray();
    }

    // Everyday words that appear in many tool descriptions without saying what a tool is for ("send a text message" is not read_chat).
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "send", "text", "message", "messages", "tell", "give", "show", "make", "create", "need", "want", "please", "something", "file", "files", "user", "the", "and", "for", "with", "from", "this", "that", "what", "does", "can", "get", "use", "new", "all", "any", "help", "write", "read", "list", "open",
    };

    /// <summary>A match on a tool's name or group counts. A match in its description counts only for a word that actually says something, and only when two such words agree.</summary>
    private static int Score(string name, string[] words, string[] typed)
    {
        string group = ToolRegistry.GroupOf(name).ToLowerInvariant(), desc = ToolRegistry.DescriptionFor(name).ToLowerInvariant(), n = name.ToLowerInvariant();
        string groupText = ToolGroups.TryGet(group, out ToolGroupDef def) ? def.Description.ToLowerInvariant() : "";
        int strong = 0, weakWords = 0, weak = 0;
        foreach (string w in words)
        {
            if (n.Contains(w)) strong += 4;
            if (group.Contains(w)) strong += 2;
            if (Generic.Contains(w)) continue;
            bool inDesc = desc.Contains(w), inGroup = groupText.Contains(w);
            if (inDesc) weak += 1;
            if (inGroup) weak += 1;
            if (inDesc || inGroup) weakWords++;
        }
        return strong > 0 ? strong + weak : weakWords >= 2 ? weak : 0;
    }
}
