using ARI.Common;

namespace ARI.LLM;

/// <summary>One tool a plugin brings. <paramref name="Create"/> makes the tool for a conversation, or returns null when it doesn't apply there.</summary>
public sealed record ProvidedTool(string Name, string Description, Func<Thread, Tool?> Create);

/// <summary>
/// A plugin that adds tools to Ari. The tools belong to the plugin: they exist only while it is switched on and running, and they go from
/// her tool list, and from any conversation that had already loaded them, the moment it stops.
/// </summary>
public interface IToolProvider
{
    IReadOnlyList<ProvidedTool> Tools { get; }

    /// <summary>What the plugin's tools are for, as one line. Shown in her tool list and on the plugin's page.</summary>
    string ToolsDescription { get; }
}

internal static class PluginTools
{
    private static IEnumerable<(IPlugin Plugin, IToolProvider Provider)> Providers() =>
        PluginRegistry.All.Where(p => p is IToolProvider).Select(p => (p, (IToolProvider)p));

    internal static IEnumerable<string> Names => Providers().SelectMany(x => x.Provider.Tools.Select(t => t.Name)).ToList();

    internal static (IPlugin Plugin, ProvidedTool Tool)? Find(string name)
    {
        foreach ((IPlugin plugin, IToolProvider provider) in Providers())
            if (provider.Tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } tool) return (plugin, tool);
        return null;
    }

    internal static bool IsProvided(string name) => Find(name) is not null;

    internal static bool IsAvailable(IPlugin plugin) => plugin.Enabled && plugin.State == PluginState.Running;

    /// <summary>Why a plugin's tool can't be used right now, in words she can repeat to the owner.</summary>
    internal static string WhyUnavailable(IPlugin plugin) =>
        !plugin.Enabled ? $"the {plugin.Name} plugin is switched off"
        : plugin.State == PluginState.Error ? $"the {plugin.Name} plugin has a problem ({plugin.Status})"
        : $"the {plugin.Name} plugin isn't running ({plugin.Status ?? "no details"})";

    /// <summary>Each plugin's tools as a group, named after the plugin, for the tool list.</summary>
    internal static Dictionary<string, ToolGroupDef> Groups() =>
        Providers().ToDictionary(x => x.Plugin.Id + "_tools", x => new ToolGroupDef(x.Provider.ToolsDescription, x.Provider.Tools.Select(t => t.Name).ToArray()), StringComparer.OrdinalIgnoreCase);
}
