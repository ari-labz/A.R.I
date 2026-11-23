[Tool("cephalon", "The Cephalon: an echo of the owner built from their words and their brain. Actions: status (how much it has learned), profile (read what it knows: voice, mind, life, people), ask (how would they answer this question, in their own words), update (rebuild it now).", Access = ToolAccess.Read)]
public class CephalonTool : Tool
{
    [Param("status, profile, ask or update.")]
    public string Action;

    [Param("For ask: the question or message to answer as them. For profile: one part (voice, mind, life or people) or leave out for all.", Optional = true)]
    public string Text;

    public override async Task<string> Run()
    {
        var plugin = (CephalonPlugin)ScriptPlugin.Get("cephalon");
        switch ((Action ?? "").Trim().ToLowerInvariant())
        {
            case "status": return plugin.Progress();
            case "update": return await plugin.Refresh();
            case "profile":
            {
                var parts = string.IsNullOrWhiteSpace(Text) ? CephalonPlugin.Parts : CephalonPlugin.Parts.Where(p => p.Equals(Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                var found = parts.Where(p => File.Exists(plugin.PartFile(p))).Select(p => $"# {p}\n{File.ReadAllText(plugin.PartFile(p)).Trim()}").ToList();
                return found.Count > 0 ? string.Join("\n\n", found) : "The profile is empty so far. Use update once there is something to learn from.";
            }
            case "ask":
            {
                if (string.IsNullOrWhiteSpace(Text)) throw new ToolArgException("Give the question in text.");
                if (!CephalonPlugin.Parts.Any(p => File.Exists(plugin.PartFile(p)))) return "The Cephalon has no profile yet, so it can't answer as them.";
                string answer = await ScriptPlugin.AskModel(plugin.PersonaText() + $"\n\nSomeone says to you:\n{Text}\n\nReply as {plugin.Owner} would, in their voice. Just the reply.");
                return answer.Trim().Length > 0 ? answer.Trim() : "The model didn't answer.";
            }
            default: throw new ToolArgException("Action is status, profile, ask or update.");
        }
    }
}
