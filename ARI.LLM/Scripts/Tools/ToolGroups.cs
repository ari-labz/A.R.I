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
/// agent registers a matching RequestTools factory, since a tool like git needs a root path that
/// only the owning agent has (see MemoryAgent.RegisterTools). ToolGroups itself stays context-free so
/// list_tools can be registered once, globally, for every thread.
/// </summary>
internal static class ToolGroups
{
    // What ToolGroups.json ships, and the groups after the user's moves (see ToolRegistry) are applied to it.
    private static Dictionary<string, ToolGroupDef> _base   = new(StringComparer.OrdinalIgnoreCase);
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
            _base = JsonSerializer.Deserialize<Dictionary<string, ToolGroupDef>>(groupsEl.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        Rebuild();
    }

    /// <summary>Recomputes the groups from the shipped ones plus wherever the user has moved tools. A group nobody
    /// is in any more disappears; a name the user invents becomes a new group.</summary>
    internal static void Rebuild()
    {
        Dictionary<string, List<string>> members = new(StringComparer.OrdinalIgnoreCase);
        foreach (string g in _base.Keys) members[g] = new();
        foreach (string name in _base.Values.SelectMany(d => d.Tools).Concat(ToolFactories.AllNames()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? g = ToolRegistry.GroupOverride(name) ?? BaseGroupOf(name);
            if (string.IsNullOrEmpty(g)) continue;
            if (!members.TryGetValue(g, out List<string>? list)) members[g] = list = new();
            list.Add(name);
        }
        _groups = members.Where(m => m.Value.Count > 0).ToDictionary(
            m => m.Key,
            m => new ToolGroupDef(_base.TryGetValue(m.Key, out ToolGroupDef? d) ? d.Description : "A group you made.", m.Value.ToArray()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The group ToolGroups.json puts a tool in, ignoring the user's moves. Null when it has none.</summary>
    internal static string? BaseGroupOf(string toolName)
        => _base.FirstOrDefault(g => g.Value.Tools.Contains(toolName, StringComparer.OrdinalIgnoreCase)).Key;

    internal static IEnumerable<string> AllNames() => _groups.Keys;
    internal static bool IsShipped(string group) => _base.ContainsKey(group);

    internal static bool TryGet(string name, out ToolGroupDef def) => _groups.TryGetValue(name, out def!);

    /// <summary>Every tool name that belongs to at least one group — used to find the leftover
    /// ungrouped tools that list_tools/request_tools must still surface individually.</summary>
    internal static HashSet<string> AllGroupedToolNames()
        => new(_groups.Values.SelectMany(g => g.Tools), StringComparer.OrdinalIgnoreCase);

    /// <summary>The always-resident manifest content: every group's name and one-liner. This is what
    /// list_tools returns, and it's cheap enough (names + descriptions, no schemas) to answer from memory.</summary>
    internal static string ManifestText()
    {
        // A group whose tools are all switched off in the control panel isn't offered at all.
        List<KeyValuePair<string, ToolGroupDef>> live = _groups.Where(g => g.Value.Tools.Any(ToolRegistry.IsEnabled)).ToList();
        return live.Count == 0
            ? "No tool groups are currently defined."
            : string.Join("\n", live.Select(g => $"- {g.Key}: {g.Value.Description}"));
    }
}
