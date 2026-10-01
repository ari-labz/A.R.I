using ARI.BrainVault;
using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// The single global tool-construction registry. Every deferrable tool name maps to ONE factory, callable
/// for any thread regardless of which agent is running on it — there is no per-agent allowlist. Whether a
/// tool actually resolves depends only on what context the thread has bound (Thread.FilesystemRoot etc.), the
/// same way a real assistant can't edit files with no project open, whoever's asking. Trust that an agent
/// won't reach for a group it has no business touching is a prompting concern (each agent's system prompt),
/// not something enforced here.
/// </summary>
internal static class ToolFactories
{
    private static readonly Dictionary<string, Func<Thread, Tool?>> _factories = new(StringComparer.OrdinalIgnoreCase)
    {
        // The one git tool: auto-discovers the repos in the bound folder (project or Brain vault) so ARI
        // never constructs paths. Every commit it makes is signed via AriGit.
        ["git"] = t => t.FilesystemRoot is { } r ? GitMulti.Discover(r, t) : null,

        // Clones a repo from any host into the project — the one thing GitMulti can't do, since it only
        // discovers repos that already exist on disk. Never in the Brain vault: the memory agents preload
        // git_tools for commits, and nothing should ever clone into the Brain.
        ["git_clone"] = t => t.FilesystemRoot is { } r && !IsVault(t, r) ? new GitClone(r) : null,

        // GitHub through gh, as the account connected in the control panel. Only in the admin's own app
        // chats — never Discord, guests or background agents — and only once gh has been provisioned.
        ["github"] = t => t.IsAdminChat && GitHubStore.ResolveToken() is not null && GhCli.ExecutablePath is { } gh
            ? new GitHubTool(gh, t.FilesystemRoot, t) : null,

        ["deliver_file"]      = t => new DeliverFile(t.Key),
        ["create_scratchpad"] = t => new CreateScratchpad(t),

        ["preview_file"]   = t => Fs(t) is { } fs ? new PreviewFile(fs)   : null,
        ["read_file"]      = t => Fs(t) is { } fs ? new Read(fs)      : null,
        ["list_directory"] = t => Fs(t) is { } fs ? new ListDirectory(fs) : null,
        ["search_files"]   = t => Fs(t) is { } fs ? new SearchFiles(fs)   : null,
        ["find_files"]     = t => Fs(t) is { } fs ? new FindFiles(fs)     : null,
        ["edit_file"]      = t => Fs(t) is { } fs ? new EditFile(fs)      : null,
        ["write_file"]     = t => Fs(t) is { } fs ? new WriteFile(fs)     : null,

        ["build_project"] = t => t is { FilesystemRoot: { } r, IsRemoteProject: false } ? new BuildProjectTool(t, r) : null,

        // No index, no database, nothing shared with ARI.Brain — see SearchVault.cs.
        ["search_vault"] = t => Fs(t) is { } fs ? new SearchVault(fs) : null,

        // project_tools — reach project creation only through Modules.Projects (ARI.Common's
        // IProjectService), never a direct ARI.API reference. Unavailable if nothing's registered it
        // yet (shouldn't happen post-startup, but the null-check keeps this consistent with every
        // other "not available in this context" factory here).
        ["list_projects"]     = _ => Modules.Projects is not null ? new ListProjects()     : null,
        ["create_project"]    = _ => Modules.Projects is not null ? new CreateProject()    : null,
        ["rename_project"]    = _ => Modules.Projects is not null ? new RenameProject()    : null,
        ["bind_project"]      = t => Modules.Projects is not null ? new BindProject(t)     : null,
        ["set_project_path"]  = _ => Modules.Projects is not null ? new SetProjectPath()   : null,

        // calendar_tools — reach the calendar only through ICalendarModule (ARI.Common), never a
        // direct reference to ARI.Calendar's domain classes. Always available once the module is up.
        ["create_event"]    = _ => Modules.Calendar is not null ? new CreateEvent()    : null,
        ["create_reminder"] = _ => Modules.Calendar is not null ? new CreateReminder() : null,
        ["list_events"]     = _ => Modules.Calendar is not null ? new ListEvents()     : null,
        ["delete_entry"]    = _ => Modules.Calendar is not null ? new DeleteEntry()    : null,

        // Available on any thread — the persona is global, not project-bound.
        ["propose_persona_edit"] = t => new ProposePersonaEdit(t),

        // Brain tools — always available; use the global brain index and vault, no project binding needed.
        ["search_brain"]   = _ => new SearchBrain(),
        ["recall_memory"]  = _ => new RecallMemory(),
        ["create_memory"]  = _ => Brain.Ready ? new CreateMemory() : null,
        ["edit_memory"]    = _ => Brain.Ready ? new EditMemory() : null,
        ["delete_memory"]  = _ => Brain.Ready ? new DeleteMemory() : null,
        ["get_time"]       = _ => new GetTime(),
        ["recent_activity"] = _ => new RecentActivity(),

        // Web tools — always available regardless of project/vault context.
        ["search_web"]  = _ => new SearchWeb(),
        ["fetch_page"]  = _ => new FetchPage(),

        ["discord_list_voice_channels"] = _ => Modules.Discord is not null ? new DiscordListVoiceChannels() : null,
        ["discord_join_voice_channel"]  = _ => Modules.Discord is not null ? new DiscordJoinVoiceChannel()  : null,
        ["discord_leave_voice_channel"] = _ => Modules.Discord is not null ? new DiscordLeaveVoiceChannel() : null,
        ["discord_dm_user"]             = t => Modules.Discord is not null ? new DiscordDmUser(t)          : null,
        ["discord_close_dm"]            = _ => Modules.Discord is not null ? new DiscordCloseDm()          : null,

        // Image generation — only available when the ImageGen module is enabled and ComfyUI is ready.
        // subagent_tools — conversations only: never on internal threads (no nesting) or Discord server channels.
        ["spawn_agent"]    = t => SubagentManager.AvailableFor(t) ? new SpawnAgent(t)   : null,
        ["wait_for_agent"] = t => SubagentManager.AvailableFor(t) ? new WaitForAgent(t) : null,
        ["cancel_agent"]   = t => SubagentManager.AvailableFor(t) ? new CancelAgent(t)  : null,

        ["generate_image"] = t => Modules.ImageGen?.IsReady == true ? new GenerateImage(t) : null,
        ["present_image"]  = t => Modules.ImageGen?.IsReady == true ? new PresentImage(t)  : null,
    };

    private static bool IsVault(Thread t, string root) => t.IsBrainVault || Directory.Exists(Path.Combine(root, ".obsidian"));

    private static ServerFileSystem? Fs(Thread t)
    {
        if (t.FilesystemRoot is not { } r) return null;
        bool isVault = t.IsBrainVault || Directory.Exists(Path.Combine(r, ".obsidian"));
        return new ServerFileSystem(r, t.Ct, brainVault: isVault, snapshotSource: () => t.Snapshots);
    }

    // Filesystem tool names that unlock when a FilesystemRoot is set.
    // Read-only subset registered for dream/read-only agents; full set for normal agents.
    private static readonly string[] FilesystemReadToolNames  = ["preview_file", "read_file", "list_directory", "search_files", "find_files", "search_vault"];
    private static readonly string[] FilesystemWriteToolNames = ["edit_file", "write_file", "deliver_file", "create_scratchpad", "build_project"];

    /// <summary>
    /// Registers all filesystem tools that now resolve for the thread. Call this immediately after
    /// setting thread.FilesystemRoot (i.e. in bind_project and create_scratchpad) so the tools are
    /// available in the same tool-call step — no OnStepComplete round-trip needed.
    /// Pass readOnly=true to register only the Read-access subset (used by the Dreamer).
    /// </summary>
    internal static void RegisterFilesystemTools(Thread thread, bool readOnly = false)
    {
        IEnumerable<string> names = readOnly
            ? FilesystemReadToolNames
            : FilesystemReadToolNames.Concat(FilesystemWriteToolNames);

        foreach (string name in names)
            if (TryBuild(name, thread, out Tool tool))
                tool.Register(thread);
    }

    internal static IEnumerable<string> AllNames() => _factories.Keys;

    /// <summary>Why a known tool didn't resolve for this thread — the real reason, so ARI doesn't blame a
    /// missing project binding for everything (e.g. git fails on a bound project with no repos in it).</summary>
    internal static string UnavailableReason(string toolName, Thread thread) => toolName.ToLowerInvariant() switch
    {
        _ when thread.IsGuarded && OWNER_ONLY_TOOLS.Contains(toolName) => "it's only available in the owner's own conversations",
        "github" when !thread.IsAdminChat              => "it's only available in the owner's own chats in the app",
        "github" when GitHubStore.ResolveToken() is null => "no GitHub account is connected — the user can connect one on the control panel's GitHub page",
        "github"                                         => "GitHub's CLI is still being installed — try again in a minute",
        "spawn_agent" or "wait_for_agent" or "cancel_agent" when SubagentManager.Agent is null
                                                         => "subagents aren't configured on this server",
        "spawn_agent" or "wait_for_agent" or "cancel_agent" => "subagents aren't available here (only in direct conversations, not Discord server channels or agent threads)",
        _ => FilesystemReason(toolName, thread),
    };

    private static string FilesystemReason(string toolName, Thread thread) => thread.FilesystemRoot switch
    {
        null => "this conversation has no project bound on the server — call bind_project first",
        { } root when toolName.Equals("git", StringComparison.OrdinalIgnoreCase) => $"no git repositories were found in {root}",
        _ => "it needs a module or connection that isn't available right now",
    };

    // Tools that reach the owner's own life, so anyone else she talks to never gets them.
    private static readonly HashSet<string> OWNER_ONLY_TOOLS = new(StringComparer.OrdinalIgnoreCase)
    {
        "search_brain", "recall_memory", "create_memory", "edit_memory", "delete_memory", "neighbours",
        "create_event", "create_reminder", "list_events", "delete_entry", "propose_persona_edit",
    };

    internal static bool TryBuild(string toolName, Thread thread, out Tool tool)
    {
        tool = null!;
        if (thread.IsGuarded && OWNER_ONLY_TOOLS.Contains(toolName)) return false;
        if (!_factories.TryGetValue(toolName, out Func<Thread, Tool?>? factory)) return false;
        if (factory(thread) is not { } built) return false;
        tool = built;
        return true;
    }

    /// <summary>Registers every tool in a group that resolves for this thread. The one code path behind
    /// both request_tools and an agent's eager PreloadedTools — "make a group real" happens exactly once.</summary>
    internal static (List<Tool> Loaded, List<string> Unavailable) LoadGroup(string group, Thread thread)
    {
        List<Tool> loaded = new(); List<string> unavailable = new();
        if (ToolGroups.TryGet(group, out ToolGroupDef def))
            foreach (string name in def.Tools)
                if (TryBuild(name, thread, out Tool tool)) { tool.Register(thread); loaded.Add(tool); }
                else unavailable.Add(name);
        return (loaded, unavailable);
    }

    /// <summary>build_project as a Tool, constructible from just a Thread + root — no owning agent needed
    /// (Coder.BuildTouched/BuildRemote are static; they don't touch instance state).</summary>
    private sealed class BuildProjectTool : Tool
    {
        private readonly Thread thread; private readonly string root;
        internal BuildProjectTool(Thread thread, string root) { this.thread = thread; this.root = root; }

        internal override string Name   => "build_project";
        internal override object Schema => Coder.BuildProjectSchema;
        internal override Func<string, string>? Display => _ => "<!--ari-tool-start:build_project:project-->";

        internal override Task<ToolResult> Execute(string argsJson)
            => Coder.BuildTouched(thread.TouchedFiles, root, thread.Ct).AsToolResult();
    }
}
