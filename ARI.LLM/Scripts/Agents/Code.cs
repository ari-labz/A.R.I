using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.LLM;

internal class Code : Agent
{
    [JsonPropertyName("shortTermMemoryLimit")] public int ShortTermMemoryLimit { get; init; }

    internal override int  MemoryLimit      => ShortTermMemoryLimit;
    internal override bool SuppressPromptLog => true;

    internal override ThreadType Type => ThreadType.Code;

    internal Code() { }

    // ── Per-thread code context ──────────────────────────────────────────────

    public readonly record struct TodoItem(string Content, string Status);

    private sealed class CodeThreadState
    {
        public string?        CodingConventions { get; set; }
        public string?        ProjectRules      { get; set; }
        public string?        ProjectMap        { get; set; }
        public List<TodoItem> Todos             { get; } = new();
        public event Action?  Updated;
        public void RaiseUpdated()             => Updated?.Invoke();
        public void SubscribeUpdated(Action a) => Updated += a;
    }

    private readonly ConcurrentDictionary<string, CodeThreadState> threadStates = new();

    private CodeThreadState GetOrCreateState(string threadKey)
        => threadStates.GetOrAdd(threadKey, _ => new CodeThreadState());

    internal void SetThreadContext(string threadKey, string? map, string? conventions, string? rules)
    {
        CodeThreadState s = GetOrCreateState(threadKey);
        s.ProjectMap        = map;
        s.CodingConventions = conventions;
        s.ProjectRules      = rules;
    }

    // ── Context building ─────────────────────────────────────────────────────

    private string BuildStaticContext(CodeThreadState s)
    {
        StringBuilder sb = new();
        void Block(string title, string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.Append("\n\n").Append(title).Append('\n').Append(body.Trim());
        }
        Block("## Coding conventions", s.CodingConventions);
        Block("## Project rules",      s.ProjectRules);
        Block("## Project map",        s.ProjectMap);
        return sb.ToString();
    }

    private string RenderTodoBlock(CodeThreadState s)
    {
        if (s.Todos.Count == 0) return "";
        StringBuilder sb = new("\n\n## Task checklist (keep current with update_todos)\n");
        foreach (TodoItem t in s.Todos)
        {
            string box = t.Status switch { "completed" => "[x]", "in_progress" => "[~]", _ => "[ ]" };
            sb.Append(box).Append(' ').Append(t.Content).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    internal string ReplaceTodos(string threadKey, string argsJson)
    {
        CodeThreadState s = GetOrCreateState(threadKey);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            s.Todos.Clear();
            if (doc.RootElement.TryGetProperty("todos", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string content = el.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
                    string status  = el.TryGetProperty("status",  out JsonElement sv) ? sv.GetString() ?? "pending" : "pending";
                    if (status is not ("pending" or "in_progress" or "completed")) status = "pending";
                    if (!string.IsNullOrWhiteSpace(content)) s.Todos.Add(new TodoItem(content.Trim(), status));
                }
            }
            s.RaiseUpdated();
            int done = s.Todos.Count(t => t.Status == "completed");
            string body = RenderTodoBlock(s);
            return $"Checklist updated — {done}/{s.Todos.Count} complete.{(body.Length > 0 ? "\n" + body : "")}";
        }
        catch (Exception ex) { return $"Error updating checklist: {ex.Message}"; }
    }

    // ── Virtual hook overrides ───────────────────────────────────────────────

    internal override string BuildPersistentContext(Thread thread)    => BuildStaticContext(GetOrCreateState(thread.Key));
    internal override string RenderDynamicContextBlock(Thread thread) => RenderTodoBlock(GetOrCreateState(thread.Key));
    internal override int    IncompleteTasks(Thread thread)           => GetOrCreateState(thread.Key).Todos.Count(t => t.Status != "completed");
    internal override bool   HasTasks(Thread thread)                  => GetOrCreateState(thread.Key).Todos.Count > 0;
    internal override string PendingTaskSummary(Thread thread)        => string.Join("\n", GetOrCreateState(thread.Key).Todos.Where(t => t.Status != "completed").Select(t => $"- {t.Content} ({t.Status})"));

    protected override void OnThreadCreated(string threadKey, Thread thread)
    {
        base.OnThreadCreated(threadKey, thread);
        thread.BecameInactive += () => thread.MarkEngramProcessed();
    }
}
