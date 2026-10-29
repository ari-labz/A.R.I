using System.Text;

namespace ARI.LLM;

/// <summary>The part of Ari's "what you can do" text that comes from what's actually switched on: the tools she's holding right
/// now, how to find the rest, and the skills she's saved. Built from the pinned tools, not from whatever she has loaded so far, so
/// it stays the same through a conversation and the prompt stays cached. Capabilities.md is still hers to edit by hand.</summary>
internal static class CapabilitiesBlock
{
    internal static string For(Thread thread)
    {
        List<string> ready = ToolRegistry.PinnedNames().Where(thread.tools.ContainsKey).ToList();
        bool owner = !thread.IsGuarded;
        StringBuilder sb = new();

        if (ready.Count > 0)
            sb.AppendLine($"You already have these tools in hand, no loading needed: {string.Join(", ", ready)}.");
        int others = ToolFactories.AllNames().Count(n => !thread.tools.ContainsKey(n) && ToolRegistry.IsEnabled(n));
        if (others > 0)
            sb.AppendLine($"There are about {others} more. When a job needs something you don't have, call tool_search with what you want to do " +
                          "(\"read a pdf\", \"send an http request\", \"look at a website\") before you tell the user you can't. Loaded tools stay for the conversation.");

        if (owner && ToolRegistry.IsEnabled("write_tool"))
            sb.AppendLine("You can write tools for yourself (tool_docs, then write_tool): if a job needs a capability that doesn't exist, build it, test it with test_tool, and use it.");
        if (owner && ToolRegistry.IsEnabled("run_code"))
            sb.AppendLine("You can run code (run_code, shell) to calculate, process files, test an idea or check something instead of guessing. Check your work before saying it's done.");
        if (owner && ToolRegistry.IsEnabled("list_chats"))
            sb.AppendLine("If you feel you're missing context, list_chats and read_chat show your other recent conversations.");

        if (owner)
        {
            var skills = SkillStore.All();
            if (skills.Count > 0)
            {
                sb.AppendLine("Skills you've saved for yourself (read one with read_skill before starting that kind of job):");
                foreach (var s in skills.Take(40)) sb.AppendLine($"- {s.Name}: {s.Description}");
            }
            else if (ToolRegistry.IsEnabled("write_skill"))
                sb.AppendLine("When you work out how to do something worth repeating, save the steps with write_skill so next time you can read them instead of starting again.");
        }
        return sb.ToString().TrimEnd();
    }
}
