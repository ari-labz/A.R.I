using ARI.Common;

namespace ARI.LLM;

/// <summary>Spawns a scratchpad — an EPHEMERAL filesystem for THIS thread, deleted when the thread ends. A
/// scratchpad is not a project: a project is a persistent filesystem a conversation is scoped to; a scratchpad
/// is throwaway space for writing files or juggling several files without needing a project at all. It becomes
/// the thread's filesystem root (the same field a project would set) and hot-loads filesystem_tools so
/// write_file / read_file / deliver_file work this turn.</summary>
internal sealed class CreateScratchpad : Tool
{
    private readonly Thread thread;
    internal CreateScratchpad(Thread thread) => this.thread = thread;

    internal override string Name => "create_scratchpad";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "create_scratchpad",
            description =
                "Spawn a scratchpad — a temporary, throwaway filesystem for THIS conversation — so you can "
              + "write files or work with several files without needing a project. Use it e.g. to give the user "
              + "a downloadable file: call create_scratchpad, then write_file, then deliver_file. The scratchpad "
              + "is deleted when the conversation ends, so don't use it for anything meant to persist (that's a "
              + "project). No effect if you already have a project or scratchpad.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (thread.FilesystemRoot is { Length: > 0 })
            return Task.FromResult<ToolResult>("You already have a working directory — use write_file to create the file, then deliver_file to give it to the user.");

        string dir = Paths.ScratchpadDir(thread.Key);
        Directory.CreateDirectory(dir);
        thread.FilesystemRoot = dir;
        ToolFactories.LoadGroup("filesystem_tools", thread);
        return Task.FromResult<ToolResult>("Scratchpad ready. Now write the file with write_file, then hand it over with deliver_file.");
    }
}
