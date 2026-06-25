using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>The agent's task checklist. Replaces the whole list each call and stores it on the
/// owning <see cref="Thread"/> (in-process — never round-trips to a client). The list is surfaced
/// to the model as a persistent context block and to the user as a checklist card.</summary>
internal sealed class UpdateTodos : Tool
{
    private readonly Coder  code;
    private readonly Thread thread;

    internal UpdateTodos(Coder code, Thread thread) { this.code = code; this.thread = thread; }

    internal override string Name => "update_todos";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "update_todos",
            description = "Maintain your task checklist for multi-step work. Pass the COMPLETE list every call — it replaces the previous one. Mark an item in_progress before you start it and completed the moment it's done; keep at most one item in_progress. Use this for any task with more than two steps (include updating tests and building as items), and do not finish while items are still pending.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    todos = new
                    {
                        type  = "array",
                        items = new
                        {
                            type       = "object",
                            properties = new
                            {
                                content = new { type = "string", description = "Short imperative task description." },
                                status  = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" }, description = "pending | in_progress | completed" }
                            },
                            required = new[] { "content", "status" }
                        }
                    }
                },
                required = new[] { "todos" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string result = code.ReplaceTodos(thread.Key, argsJson);
        thread.RaiseUpdated();
        return Task.FromResult(result);
    }

    // Start marker (instant), then an end marker carrying the list base64-encoded as
    // "status\tcontent" lines — the UI decodes it into a checklist card.
    internal override Func<string, string>? Display => _ => "<!--ari-tool-start:update_todos:checklist-->";

    internal override Func<string, string>? DisplayAfter => args =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(args);
            StringBuilder sb = new();
            if (doc.RootElement.TryGetProperty("todos", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string content = el.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                    string status  = el.TryGetProperty("status",  out JsonElement s) ? s.GetString() ?? "pending" : "pending";
                    sb.Append(status).Append('\t').Append(content.Replace("\n", " ").Replace("\t", " ")).Append('\n');
                }
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString().TrimEnd()));
            return $"<!--ari-tool-end:update_todos:{b64}-->";
        }
        catch { return "<!--ari-tool-end:update_todos:-->"; }
    };
}
