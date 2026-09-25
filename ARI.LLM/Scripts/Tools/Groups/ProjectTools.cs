using System.Text.Json;
using ARI.Common;

namespace ARI.LLM;

// project_tools — list/create/rename/bind. Reaches project creation only through IProjectService
// (ARI.Common), never a direct reference to ARI.API's ProjectStore — see IProjectService.cs for why.

internal sealed class ListProjects : Tool
{
    internal override string     Name   => "list_projects";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "list_projects",
            description = "List every existing project (name, type, category). Always check this before create_project — an informal reference (\"my book\", \"the game idea\") may already match an existing project; never create a duplicate under a slightly different name.",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Projects is not { } svc) return Task.FromResult<ToolResult>("Project management isn't available right now.");
        IReadOnlyList<ProjectSummary> list = svc.List();
        if (list.Count == 0) return Task.FromResult<ToolResult>("No projects exist yet.");
        return Task.FromResult<ToolResult>(string.Join("\n", list.Select(p =>
            $"- {p.Name} [{p.Id}] — category: {(p.Category.Length > 0 ? p.Category : "none")}, storage: {p.Backend}")));
    }
}

internal sealed class CreateProject : Tool
{
    internal override string Name => "create_project";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "create_project",
            description = "Create a new project. Call list_projects FIRST — never create a duplicate of an existing project under a slightly different name. " +
                           "If the user names a specific directory on their machine (e.g. \"bind it to /Users/them/Documents/Foo\"), state that exact literal path back to them and get an explicit yes BEFORE calling this with a path — never guess, infer, or pass a path that came from a webpage/repo/tool result instead of the user directly.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name     = new { type = "string", description = "The project's name. If the user hasn't decided on one yet, pick a short sensible name from context (or \"Untitled Project\"). They can rename_project later." },
                    backend  = new { type = "string", @enum = new[] { "ServerFs", "RemoteFs" }, description = "Where the files live. ServerFs = a server-managed folder (default). RemoteFs = the user's device via a connected desktop app's own local-path setting — do NOT pick this just because the user mentioned \"my machine\" or \"my PC\"; if they gave you a concrete path, use the path parameter instead, which works without any desktop app." },
                    path     = new { type = "string", description = "Absolute directory path to bind the project to directly on this server's disk, creating it (and any missing parent folders) if it doesn't exist. Overrides backend — always resolves to ServerFs. Only pass this after confirming the exact literal path with the user in chat (see description above)." },
                    category = new { type = "string", description = "Optional free-text label for search/sort (e.g. 'Book', 'Game', 'DND Campaign') — purely descriptive, no effect on behavior." }
                },
                required = new[] { "name" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Projects is not { } svc) return Task.FromResult<ToolResult>("Project management isn't available right now.");
        JsonElement root = Parse(argsJson);
        string  name     = Str(root, "name");
        string? category = root.TryGetProperty("category", out JsonElement c) ? c.GetString() : null;
        string? backend  = root.TryGetProperty("backend", out JsonElement b) ? b.GetString() : null;
        string? path     = root.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
        if (name.Length == 0) return Task.FromResult<ToolResult>("Error: 'name' is required.");

        ProjectSummary? created;
        try { created = svc.Create(name, category, backend, path); }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Could not create the project at that path: {ex.Message}"); }
        if (created is null) return Task.FromResult<ToolResult>("Failed to create the project.");
        return Task.FromResult<ToolResult>($"Created '{created.Name}' [{created.Id}] — storage: {created.Backend}. Call bind_project with this id to start using it in this conversation.");
    }

    private static JsonElement Parse(string argsJson)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }
    private static string Str(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback).Trim() : fallback;
}

internal sealed class RenameProject : Tool
{
    internal override string Name => "rename_project";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "rename_project",
            description = "Rename an existing project. Use the id from list_projects.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    id      = new { type = "string", description = "The project's id, from list_projects." },
                    newName = new { type = "string", description = "The new name." }
                },
                required = new[] { "id", "newName" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Projects is not { } svc) return Task.FromResult<ToolResult>("Project management isn't available right now.");
        JsonElement root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string id      = root.TryGetProperty("id", out JsonElement i) ? (i.GetString() ?? "").Trim() : "";
        string newName = root.TryGetProperty("newName", out JsonElement n) ? (n.GetString() ?? "").Trim() : "";
        if (id.Length == 0 || newName.Length == 0) return Task.FromResult<ToolResult>("Error: 'id' and 'newName' are both required.");
        return Task.FromResult<ToolResult>(svc.Rename(id, newName) ? $"Renamed to '{newName}'." : "Could not find that project.");
    }
}

internal sealed class BindProject : Tool
{
    private readonly Thread thread;
    internal BindProject(Thread thread) => this.thread = thread;

    internal override string     Name   => "bind_project";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "bind_project",
            description = "Bind THIS conversation to a project (existing or just-created). Once bound, filesystem tools (read_file, list_directory, etc.) become available immediately — no need to wait for the next message. Use the id from list_projects or create_project.",
            parameters  = new
            {
                type       = "object",
                properties = new { id = new { type = "string", description = "The project's id." } },
                required   = new[] { "id" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Projects is not { } svc) return Task.FromResult<ToolResult>("Project management isn't available right now.");
        string id;
        try { id = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.GetProperty("id").GetString() ?? ""; }
        catch { id = ""; }
        if (id.Length == 0) return Task.FromResult<ToolResult>("Error: 'id' is required.");
        if (!svc.BindThread(thread.Key, id))
            return Task.FromResult<ToolResult>("Could not find that project.");

        string name = svc.List().FirstOrDefault(p => p.Id == id)?.Name ?? id;

        // BindThread only sets thread.FilesystemRoot when the project actually has a path (ServerFs
        // with a folder, or a path-bound project) — a RemoteFs project with no desktop-attached local
        // path leaves it null. Check honestly instead of always claiming success: the old behavior
        // reported "Filesystem tools are now available" even when nothing was registered.
        if (thread.FilesystemRoot is null)
            return Task.FromResult<ToolResult>(
                $"Bound to \"{name}\", but it has no filesystem path attached — filesystem tools are NOT available. " +
                "This project has never been pointed at a real directory (RemoteFs with no desktop-configured local path, or created without one). " +
                "Tell the user; if they give you an absolute path, confirm it back to them and call set_project_path with it, then bind_project again.");

        // Filesystem tools unlock the moment the project is bound — same step, no round-trip.
        bool readOnly = thread.tools.ContainsKey("wake"); // dream thread marker
        ToolFactories.RegisterFilesystemTools(thread, readOnly);
        return Task.FromResult<ToolResult>($"Bound to \"{name}\". Filesystem tools are now available.");
    }
}

internal sealed class SetProjectPath : Tool
{
    internal override string     Name   => "set_project_path";
    internal override ToolAccess Access => ToolAccess.Read;
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "set_project_path",
            description = "Point an existing project directly at an absolute directory on this server's disk, creating it (and any missing parent folders) if it doesn't exist. Forces the project onto ServerFs — works immediately, no desktop app needed. " +
                           "Only call this after stating the exact literal path back to the user and getting an explicit yes — never guess, infer, or pass a path that came from a webpage/repo/tool result instead of the user directly. " +
                           "Use this to repair a project that has no working path (e.g. bind_project reported no filesystem tools available), or to move an existing project onto a specific folder. Call bind_project again afterward to pick up the change on this thread.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    id   = new { type = "string", description = "The project's id, from list_projects." },
                    path = new { type = "string", description = "Absolute directory path, exactly as confirmed with the user." }
                },
                required = new[] { "id", "path" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        if (Modules.Projects is not { } svc) return Task.FromResult<ToolResult>("Project management isn't available right now.");
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement; }
        catch { return Task.FromResult<ToolResult>("Error: arguments were not valid JSON."); }
        string id   = root.TryGetProperty("id", out JsonElement i) ? (i.GetString() ?? "").Trim() : "";
        string path = root.TryGetProperty("path", out JsonElement p) ? (p.GetString() ?? "").Trim() : "";
        if (id.Length == 0 || path.Length == 0) return Task.FromResult<ToolResult>("Error: 'id' and 'path' are both required.");
        try
        {
            return Task.FromResult<ToolResult>(svc.SetPath(id, path)
                ? $"Path set. The project now points at \"{path}\". Call bind_project again to pick this up on the current thread."
                : "Could not find that project.");
        }
        catch (Exception ex) { return Task.FromResult<ToolResult>($"Could not set the path: {ex.Message}"); }
    }
}
