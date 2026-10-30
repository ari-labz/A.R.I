using System.Text;

namespace ARI.LLM;

/// <summary>Ari's "what you can do", generated from what is actually switched on for this conversation, so it can't go stale or promise
/// something that isn't there: the tools she holds, a map of everything else she can load (by group, with the tool names), what exists but
/// is off here and why, and how to treat a request she can or can't do. Built from the pinned tools and the groups, not from whatever she
/// has loaded so far, so it stays the same through a conversation and the prompt stays cached.</summary>
internal static class CapabilitiesBlock
{
    private const int BlurbLength = 100;

    internal static string For(Thread thread)
    {
        bool owner = !thread.IsGuarded;
        bool conversation = thread.Pipeline == ThreadPipeline.Dialogue && !thread.Internal;
        StringBuilder sb = new();

        List<string> ready = ToolRegistry.PinnedNames().Where(thread.tools.ContainsKey).ToList();
        if (ready.Count > 0)
            sb.AppendLine($"You already have these tools in hand, no loading needed: {string.Join(", ", ready)}.");

        if (conversation) AppendMap(sb, thread, owner);
        else
        {
            int others = ToolFactories.AllNames().Count(n => !thread.tools.ContainsKey(n) && ToolRegistry.IsEnabled(n));
            if (others > 0) sb.AppendLine($"There are about {others} more. Call tool_search with what you want to do when a job needs something you don't have.");
        }

        if (owner && ToolRegistry.IsEnabled("write_tool"))
            sb.AppendLine("You can write tools for yourself (tool_docs, then write_tool): if a job needs a capability that doesn't exist, build it, test it with test_tool, and use it.");
        if (owner && ToolRegistry.IsEnabled("run_code"))
            sb.AppendLine("How you work on a real job: for anything with several steps, write the plan with todo and keep it up to date. Use your tools rather than guessing or " +
                          "going from memory: run_code to calculate or process data, shell to run things, read_document for PDFs and spreadsheets, browser to look at a page. " +
                          "When a tool returns an error, read it, fix the cause and try again before you give up or ask. Check your work before saying it's done: run it, open the file, " +
                          "take a screenshot of the page you built.");
        if (owner && ToolRegistry.IsEnabled("list_chats"))
            sb.AppendLine("If you feel you're missing context, list_chats and read_chat show your other recent conversations.");

        if (conversation && thread.Medium != ThreadMedium.Channel)
            sb.AppendLine("Your files and commands are on the server you run on, not on the user's own computer or phone. You can't open a file from their device: they upload it into the chat, " +
                          "and you give them yours with deliver_file. If they mention a file on their desktop, ask them to attach it.");

        if (conversation)
            sb.AppendLine("Can and can't: if your tools, or code you run, or a tool you write, can get it done, do it. Work out the steps and fix errors yourself; don't refuse " +
                          "something these tools can do just because a generic AI assistant couldn't. If nothing here can do it, say so plainly in a sentence or two: what is missing, " +
                          "and the closest thing you can do instead. Something outside you that none of your tools reach (a bank balance, someone's phone, an inbox or account you aren't connected to) isn't something you \"don't know\": " +
                          "say you have no connection to it. Never pretend, and never invent a result. " +
                          "If you've tried several different approaches and are still not getting anywhere, stop: tell the user what you tried and what's blocking you, and ask, instead of carrying on.");

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

    /// <summary>The map on its own, for tool_search to hand back when nothing matched.</summary>
    internal static string Overview(Thread thread)
    {
        StringBuilder sb = new();
        AppendMap(sb, thread, !thread.IsGuarded);
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line per tool group she can load here, with its tool names, then what exists but is off or unavailable and why.</summary>
    private static void AppendMap(StringBuilder sb, Thread thread, bool owner)
    {
        List<string> lines = new();
        foreach (string group in ToolGroups.AllNames().OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            if (!ToolGroups.TryGet(group, out ToolGroupDef def)) continue;
            List<string> names = def.Tools.Where(n => !ToolRegistry.IsPinned(n) && !ToolFactories.Withheld(n, thread) && ToolFactories.TryBuild(n, thread, out _)).ToList();
            if (names.Count > 0) lines.Add($"- {group}: {Blurb(def.Description)} [{string.Join(", ", names)}]");
        }
        HashSet<string> grouped = ToolGroups.AllGroupedToolNames();
        List<string> loose = ToolFactories.AllNames()
            .Where(n => !grouped.Contains(n) && !ToolRegistry.IsPinned(n) && !ToolFactories.Withheld(n, thread) && ToolFactories.TryBuild(n, thread, out _))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (loose.Count > 0) lines.Add($"- other tools: {string.Join(", ", loose)}");

        if (lines.Count > 0)
        {
            sb.AppendLine("More you can load when a job needs it. Call tool_search with what you want to do (\"read a pdf\", \"resize a picture\", \"run a command on another machine\"), or request_tools with a group name:");
            foreach (string l in lines) sb.AppendLine(l);
        }

        if (!owner) return;
        Dictionary<string, List<string>> off = new(); List<string> disabled = new();
        foreach (string n in ToolFactories.AllNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (ToolFactories.Withheld(n, thread)) continue;
            if (!ToolRegistry.IsEnabled(n)) { disabled.Add(n); continue; }
            if (ToolFactories.TryBuild(n, thread, out _)) continue;
            string reason = ToolFactories.UnavailableReason(n, thread);
            if (!off.TryGetValue(reason, out List<string>? list)) off[reason] = list = new();
            list.Add(n);
        }
        if (off.Count > 0)
        {
            sb.AppendLine("These exist but can't be used in this conversation right now. If the user asks for one, say what it needs:");
            foreach (var (reason, names) in off) sb.AppendLine($"- {reason}: {string.Join(", ", names.Take(12))}");
        }
        if (disabled.Count > 0) sb.AppendLine($"Switched off by the owner in the control panel: {string.Join(", ", disabled.Take(20))}.");
    }

    private static string Blurb(string description)
    {
        string first = description.Split(". ", 2)[0].Trim().TrimEnd('.');
        if (first.Length <= BlurbLength) return first;
        int cut = first.LastIndexOf(' ', BlurbLength);
        return first[..(cut > 40 ? cut : BlurbLength)] + "…";
    }
}
