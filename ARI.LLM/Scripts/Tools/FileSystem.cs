using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// A project's file system — the seam between the file tools and HOW files are physically reached.
/// A tool calls these operations and never cares whether the project lives on the server's own disk
/// (<see cref="ServerFileSystem"/>) or on a connected client machine over the socket
/// (ClientFileSystem). Each method takes the tool's raw argsJson and returns the tool-result string,
/// so a tool is a thin wrapper: <c>Execute(args) =&gt; fs.Read(args)</c>.
///
/// Methods are virtual with an "unavailable" default so a backend implements only the operations it
/// supports (e.g. the eval has no run_command); the rest cleanly report unavailability rather than
/// throwing. The logic inside each override is relocated verbatim from the existing tools, so both
/// backends preserve their current behaviour exactly — shared-logic de-duplication is a later step.
/// </summary>
internal abstract class FileSystem
{
    // Paths the model has successfully read or previewed this session.
    // EditFile checks this to block edits on files the model has never seen.
    internal readonly HashSet<string> ReadLedger = new(StringComparer.OrdinalIgnoreCase);

    internal void MarkRead(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("path", out JsonElement p) && p.GetString() is { } path)
                ReadLedger.Add(path);
        }
        catch { }
    }


    public virtual Task<string> Read(string argsJson)    => Unavailable("read_file");
    public virtual Task<string> Preview(string argsJson) => Unavailable("preview_file");
    public virtual Task<string> Edit(string argsJson)    => Unavailable("edit_file");
    public virtual Task<string> Write(string argsJson)   => Unavailable("write_file");
    public virtual Task<string> Search(string argsJson)  => Unavailable("search_files");
    public virtual Task<string> Find(string argsJson)    => Unavailable("find_files");
    public virtual Task<string> List(string argsJson)    => Unavailable("list_directory");
    public virtual Task<string> Delete(string argsJson)  => Unavailable("delete_file");
    public virtual Task<string> Move(string argsJson)    => Unavailable("move_file");
    public virtual Task<string> Run(string argsJson)     => Unavailable("run_command");

    private static Task<string> Unavailable(string op)
        => Task.FromResult($"[Error: {op} is not available for this project.]");
}
