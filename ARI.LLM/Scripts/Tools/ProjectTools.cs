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
            description = "Create a new project. Call list_projects FIRST — never create a duplicate of an existing project under a slightly different name.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    name     = new { type = "string", description = "The project's name. If the user hasn't decided on one yet, pick a short sensible name from context (or \"Untitled Project\"). They can rename_project later." },
                    backend  = new { type = "string", @enum = new[] { "ServerFs", "RemoteFs" }, description = "Where the files live. ServerFs = stored on this server (default). RemoteFs = stored on the user's device via the desktop app." },
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
        if (name.Length == 0) return Task.FromResult<ToolResult>("Error: 'name' is required.");

        ProjectSummary? created = svc.Create(name, category, backend);
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
            description = "Bind THIS conversation to a project (existing or just-created). Once bound, filesystem_tools/obsidian_tools become usable for it immediately — no need to wait for the next message. Use the id from list_projects or create_project.",
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
        return Task.FromResult<ToolResult>(svc.BindThread(thread.Key, id)
            ? "Bound. The project's filesystem is now accessible — call list_directory with no arguments to see the root, then read_file to open anything that interests you."
            : "Could not find that project.");
    }
}
