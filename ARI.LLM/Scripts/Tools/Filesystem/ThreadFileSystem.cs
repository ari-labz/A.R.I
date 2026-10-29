using ARI.Common;

namespace ARI.LLM;

/// <summary>The file system of a conversation that has no project: each call works in its scratchpad, made the first time
/// it's needed. It lets the file tools be there from the first message, and when a project is bound later the same tools
/// follow the thread to it, because the folder is looked up on every call rather than fixed when the tool was made.</summary>
internal sealed class ThreadFileSystem : FileSystem
{
    private readonly Thread thread;

    internal ThreadFileSystem(Thread thread)
    {
        this.thread = thread;
        SnapshotSource = () => thread.Snapshots;
    }

    private ServerFileSystem Inner()
    {
        string root = thread.FilesystemRoot is { Length: > 0 } r ? r : Make();
        bool isVault = thread.IsBrainVault || Directory.Exists(Path.Combine(root, ".obsidian"));
        return new ServerFileSystem(root, thread.Ct, brainVault: isVault, snapshotSource: () => thread.Snapshots);
    }

    private string Make()
    {
        string dir = Paths.ScratchpadDir(thread.Key);
        Directory.CreateDirectory(dir);
        thread.FilesystemRoot = dir;
        return dir;
    }

    internal override bool    GuardsOverwrites        => Inner().GuardsOverwrites;
    internal override string? WriteTarget(string path) => Inner().WriteTarget(path);

    public override Task<string> Read(string argsJson)    => Inner().Read(argsJson);
    public override Task<byte[]> ReadBytes(string path)   => Inner().ReadBytes(path);
    public override Task<string> Preview(string argsJson) => Inner().Preview(argsJson);
    public override Task<string> Edit(string argsJson)    => Inner().Edit(argsJson);
    public override Task<string> Write(string argsJson)   => Inner().Write(argsJson);
    public override Task<string> Search(string argsJson)  => Inner().Search(argsJson);
    public override Task<string> Find(string argsJson)    => Inner().Find(argsJson);
    public override Task<string> List(string argsJson)    => Inner().List(argsJson);
    public override Task<string> Delete(string argsJson)  => Inner().Delete(argsJson);
    public override Task<string> Move(string argsJson)    => Inner().Move(argsJson);
}
