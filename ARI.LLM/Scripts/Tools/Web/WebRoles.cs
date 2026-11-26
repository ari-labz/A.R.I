namespace ARI.LLM;

/// <summary>
/// Which tools search the web and which read a page. Reading (<c>fetch_page</c>) is built in; searching comes from plugins, which mark
/// a search tool with <c>Card = "web_search"</c> on its <see cref="ToolAttribute"/>. Everything that treats web work specially (the
/// research budgets, the "Researching" phase, source citations, the search card, subagents, pinning) asks here, never by tool name.
/// </summary>
internal static class WebRoles
{
    /// <summary>The card name a plugin gives its search tool.</summary>
    internal const string SearchCard = "web_search";

    /// <summary>How a search tool says it found nothing relevant: a result starting with this doesn't use up the search budget.</summary>
    internal const string NoRelevantPrefix = "No relevant results for";

    internal const string ReadTool = "fetch_page";

    internal static bool IsSearch(string name) => PluginTools.Find(name)?.Tool.Card == SearchCard;
    internal static bool IsRead(string name) => name == ReadTool;
    internal static bool IsWeb(string name) => IsRead(name) || IsSearch(name);

    /// <summary>The search tools available right now (from plugins that are switched on and running).</summary>
    internal static IEnumerable<string> SearchTools() => PluginTools.Names.Where(IsSearch);
}
