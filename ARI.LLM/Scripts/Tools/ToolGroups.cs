using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>One entry from ToolGroups.json: a name the model can pass to request_tools, a one-line
/// description shown in the always-resident manifest, and the tool names that group resolves to.</summary>
internal sealed record ToolGroupDef(
    [property: JsonPropertyName("description")] string   Description,
    [property: JsonPropertyName("tools")]       string[] Tools);

/// <summary>
/// The global catalog of deferred tool groups (issue #126). Loaded once from ToolGroups.json at
/// startup. This class only knows NAMES — a group's actual Tool instances are constructed by whichever
/// agent registers a matching RequestTools factory, since a tool like git_status needs a root path that
/// only the owning agent has (see MemoryAgent.RegisterTools). ToolGroups itself stays context-free so
/// list_tools can be registered once, globally, for every thread.
/// </summary>
internal static class ToolGroups
{
    private static Dictionary<string, ToolGroupDef> _groups = new(StringComparer.OrdinalIgnoreCase);

    internal static void Load(string path)
    {
        if (!File.Exists(path))
        {
            Shared.Logger.LogError("[ToolGroups] {Path} not found — no deferred tool groups will be available.", path);
            return;
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.TryGetProperty("Groups", out JsonElement groupsEl))
            _groups = JsonSerializer.Deserialize<Dictionary<string, ToolGroupDef>>(groupsEl.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    internal static bool TryGet(string name, out ToolGroupDef def) => _groups.TryGetValue(name, out def!);

    /// <summary>The always-resident manifest content: every group's name and one-liner. This is what
    /// list_tools returns, and it's cheap enough (names + descriptions, no schemas) to answer from memory.</summary>
    internal static string ManifestText()
        => _groups.Count == 0
            ? "No tool groups are currently defined."
            : string.Join("\n", _groups.Select(g => $"- {g.Key}: {g.Value.Description}"));
}
