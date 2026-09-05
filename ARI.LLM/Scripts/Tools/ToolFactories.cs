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
        ["git_status"] = t => t.FilesystemRoot is { } r ? new GitStatus(r) : null,
        ["git_diff"]   = t => t.FilesystemRoot is { } r ? new GitDiff(r)   : null,
        ["git_log"]    = t => t.FilesystemRoot is { } r ? new GitLog(r)    : null,
        ["git_commit"] = t => t.FilesystemRoot is { } r ? new GitCommit(r, "A.R.I <ari@ari.local>") : null,

        // Multi-repo git tool: auto-discovers repos inside the project folder so ARI never constructs paths.
        ["git"] = t => t.FilesystemRoot is { } r ? GitMulti.Discover(r) : null,

        // GitHub over the REST API — no gh binary. projectRoot lets it use a project-scoped token.
        ["github"] = t => new GitHubTool(t.FilesystemRoot),

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
        ["list_projects"]  = _ => Modules.Projects is not null ? new ListProjects()  : null,
        ["create_project"] = _ => Modules.Projects is not null ? new CreateProject() : null,
        ["rename_project"] = _ => Modules.Projects is not null ? new RenameProject() : null,
        ["bind_project"]   = t => Modules.Projects is not null ? new BindProject(t)  : null,

        // Available on any thread — the persona is global, not project-bound.
        ["propose_persona_edit"] = t => new ProposePersonaEdit(t),

        // Brain tools — always available; use the global brain index and vault, no project binding needed.
        ["search_brain"]   = _ => new SearchBrain(),
        ["recall_memory"]  = _ => new RecallMemory(),
        ["create_memory"]  = _ => Brain.Ready ? new CreateMemory() : null,
        ["edit_memory"]    = _ => Brain.Ready ? new EditMemory() : null,
        ["delete_memory"]  = _ => Brain.Ready ? new DeleteMemory() : null,
        ["get_time"]       = _ => new GetTime(),

        // Web tools — always available regardless of project/vault context.
        ["search_web"]  = _ => new SearchWeb(),
        ["fetch_page"]  = _ => new FetchPage(),

        ["discord_list_voice_channels"] = _ => Modules.Discord is not null ? new DiscordListVoiceChannels() : null,
        ["discord_join_voice_channel"]  = _ => Modules.Discord is not null ? new DiscordJoinVoiceChannel()  : null,
        ["discord_leave_voice_channel"] = _ => Modules.Discord is not null ? new DiscordLeaveVoiceChannel() : null,

        // Image generation — only available when the ImageGen module is enabled and ComfyUI is ready.
        ["generate_image"] = t => Modules.ImageGen?.IsReady == true ? new GenerateImage(t) : null,
        ["present_image"]  = t => Modules.ImageGen?.IsReady == true ? new PresentImage(t)  : null,
    };

    private static ServerFileSystem? Fs(Thread t)
    {
        if (t.FilesystemRoot is not { } r) return null;
        bool isVault = t.IsBrainVault || Directory.Exists(Path.Combine(r, ".obsidian"));
        return new ServerFileSystem(r, t.Ct, t.Snapshots, isVault);
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

    internal static bool TryBuild(string toolName, Thread thread, out Tool tool)
    {
        tool = null!;
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
