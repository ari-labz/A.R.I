using System.Collections.Concurrent;

namespace ARI.LLM;

[Tool("todo", "Keep a checklist for a job with several steps, so you don't lose your place or forget a part. " +
              "Call it at the start with the whole plan, and again each time you finish a step. Send the WHOLE list every time; it replaces the last one. " +
              "Write [~] on the step you're doing now and [x] on finished ones. Before you tell the user you're done, every line should be [x] or deliberately dropped.",
      Access = ToolAccess.Read)]
public class Todo : Tool
{
    [Param("The whole list, one step per line. Start each line with [ ] (to do), [~] (doing now) or [x] (done).")]
    public string Items;

    private static readonly ConcurrentDictionary<string, string> lists = new();

    public override Task<string> Run()
    {
        string[] lines = Items.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int done = lines.Count(l => l.StartsWith("[x]", StringComparison.OrdinalIgnoreCase)), doing = lines.Count(l => l.StartsWith("[~]"));
        lists[ConversationId] = string.Join("\n", lines);
        string next = lines.FirstOrDefault(l => l.StartsWith("[~]")) ?? lines.FirstOrDefault(l => l.StartsWith("[ ]")) ?? "";
        return Task.FromResult($"Checklist saved: {done} of {lines.Length} done{(doing > 0 ? $", {doing} in progress" : "")}.\n{string.Join("\n", lines)}" +
            (done == lines.Length ? "\nEverything is done." : next.Length > 0 ? $"\nNext: {next[3..].Trim()}" : ""));
    }
}
