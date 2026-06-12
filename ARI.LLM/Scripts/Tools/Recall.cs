using System.Text;
using System.Text.Json;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal sealed class Recall : Tool
{
    private const int MAX_RESULTS = 5;

    private static readonly JsonSerializerOptions caseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private record Query(string query);

    private readonly BrainService brain;

    internal Recall(BrainService brain) => this.brain = brain;

    internal override string Name => "search_memories";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "search_memories",
            description = "Search your long-term memory for notes about people, places, events, and topics. Use this when the conversation references personal details, names, relationships, or past events.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    query = new
                    {
                        type        = "string",
                        description = "The name or topic to search for. Pass the bare title — for a path like 'People/Family/Ryan' pass 'Ryan'."
                    }
                },
                required = new[] { "query" }
            }
        }
    };

    internal override async Task<string> Execute(string argsJson)
    {
        Query? parsed = JsonSerializer.Deserialize<Query>(argsJson, caseInsensitive);
        string raw    = parsed?.query ?? string.Empty;
        string title  = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;

        if (string.IsNullOrWhiteSpace(title))
            return "No query provided.";

        Common.Logger.LogInformation("[Recall] query: '{Query}'", raw);

        List<string> allTitles = await brain.GetNoteTitles();
        List<string> matches   = allTitles
            .Where(t => t.Contains(title, StringComparison.OrdinalIgnoreCase))
            .Take(MAX_RESULTS)
            .ToList();

        if (matches.Count == 0)
        {
            Common.Logger.LogInformation("[Recall] '{Title}' — no matches.", title);
            return $"No memories found for '{title}'.";
        }

        Common.Logger.LogInformation("[Recall] '{Title}' — retrieving [{Notes}]", title, string.Join(", ", matches));

        StringBuilder result = new();
        foreach (string match in matches)
        {
            string? content = await brain.GetNote(match);
            if (content is null) continue;
            string? noteId  = await brain.GetNoteId(match);
            string? url     = noteId is not null && !string.IsNullOrEmpty(brain.BrainPublicUrl)
                                ? $"{brain.BrainPublicUrl.TrimEnd('/')}/#?note={noteId}"
                                : null;
            string header = url is not null ? $"[{match}|{url}]" : $"[{match}]";
            result.AppendLine(header);
            result.AppendLine(content);
            result.AppendLine();
        }

        return result.Length > 0 ? result.ToString().TrimEnd() : $"No memories found for '{title}'.";
    }
}
