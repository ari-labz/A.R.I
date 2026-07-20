namespace ARI.LLM;

/// <summary>
/// The single global tool-construction registry. Every deferrable tool name maps to ONE factory, callable
/// for any thread regardless of which agent is running on it — there is no per-agent allowlist. Whether a
/// tool actually resolves depends only on what context the thread has bound (Thread.ProjectRoot etc.), the
/// same way a real assistant can't edit files with no project open, whoever's asking. Trust that an agent
/// won't reach for a group it has no business touching is a prompting concern (each agent's system prompt),
/// not something enforced here.
/// </summary>
internal static class ToolFactories
{
    private static readonly Dictionary<string, Func<Thread, Tool?>> _factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git_status"] = t => t.ProjectRoot is { } r ? new GitStatus(r) : null,
        ["git_diff"]   = t => t.ProjectRoot is { } r ? new GitDiff(r)   : null,
        ["git_log"]    = t => t.ProjectRoot is { } r ? new GitLog(r)    : null,
        ["git_commit"] = t => t.ProjectRoot is { } r ? new GitCommit(r, "A.R.I <ari@ari.local>") : null,

        ["preview_file"]   = t => Fs(t) is { } fs ? new PreviewFile(fs)   : null,
        ["read_file"]      = t => Fs(t) is { } fs ? new ReadFile(fs)      : null,
        ["list_directory"] = t => Fs(t) is { } fs ? new ListDirectory(fs) : null,
        ["search_files"]   = t => Fs(t) is { } fs ? new SearchFiles(fs)   : null,
        ["find_files"]     = t => Fs(t) is { } fs ? new FindFiles(fs)     : null,
        ["edit_file"]      = t => Fs(t) is { } fs ? new EditFile(fs)      : null,
        ["write_file"]     = t => Fs(t) is { } fs ? new WriteFile(fs)     : null,

        ["build_project"] = t => t is { ProjectRoot: { } r, IsRemoteProject: false } ? new BuildProjectTool(t, r) : null,

        // No index, no database, nothing shared with ARI.Brain — see SearchVault.cs.
        ["search_vault"] = t => Fs(t) is { } fs ? new SearchVault(fs) : null,
    };

    private static ServerFileSystem? Fs(Thread t)
        => t.ProjectRoot is { } r ? new ServerFileSystem(r, t.Ct, t.Snapshots, t.IsBrainVault) : null;

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
    internal static (List<string> Loaded, List<string> Unavailable) LoadGroup(string group, Thread thread)
    {
        List<string> loaded = new(), unavailable = new();
        if (ToolGroups.TryGet(group, out ToolGroupDef def))
            foreach (string name in def.Tools)
                if (TryBuild(name, thread, out Tool tool)) { tool.Register(thread); loaded.Add(name); }
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

        internal override Task<string> Execute(string argsJson)
            => Coder.BuildTouched(thread.TouchedFiles, root, thread.Ct);
    }
}
