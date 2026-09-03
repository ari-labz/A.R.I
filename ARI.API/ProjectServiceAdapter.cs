using ARI.BrainVault;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.API;

/// <summary>
/// The real implementation of IProjectService (ARI.Common), registered into Modules.Projects at
/// startup. ProjectsController delegates Create/Rename here too, so the REST path and the tool-call
/// path (project_tools in ARI.LLM) share exactly one "create/rename a project" code path instead of
/// two that could drift apart.
/// </summary>
public class ProjectServiceAdapter(ProjectStore store) : IProjectService
{
    public IReadOnlyList<ProjectSummary> List() => store.GetAll().Select(ToSummary).ToList();

    public ProjectSummary? Create(string name, string? category, string? backend = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string id = Guid.NewGuid().ToString("N");
        StorageBackend resolvedBackend = Enum.TryParse(backend, ignoreCase: true, out StorageBackend explicitBackend)
            ? explicitBackend
            : StorageBackend.ServerFs;
        string trimmedName = name.Trim();
        string? rootPath = resolvedBackend == StorageBackend.ServerFs ? ProjectStore.CreateServerFolder(id, trimmedName) : null;

        Project project = new(
            Id:           id,
            Name:         trimmedName,
            Description:  "",
            Instructions: "",
            CreatedAt:    DateTime.UtcNow,
            Category:     category?.Trim() ?? "",
            Backend:      resolvedBackend,
            RootPath:     rootPath);

        store.Add(project);
        EnsureBrainNote(project);
        if (Modules.Llm is LLMModule llm)
            llm.BroadcastProjectsChanged();
        return ToSummary(project);
    }

    public bool Rename(string id, string newName)
    {
        Project? existing = store.Get(id);
        if (existing is null || string.IsNullOrWhiteSpace(newName)) return false;
        string trimmed = newName.Trim();
        if (trimmed == existing.Name) return true;

        store.Update(existing with { Name = trimmed });
        RenameBrainNote(existing.Name, trimmed);
        return true;
    }

    public bool BindThread(string threadKey, string projectId)
    {
        Project? project = store.Get(projectId);
        if (project is null) return false;

        store.BindThread(threadKey, projectId);
        if (Modules.Llm is LLMModule llm)
        {
            bool isVault = project.RootPath is { } rp && Directory.Exists(Path.Combine(rp, ".obsidian"));
            llm.BindProjectContext(threadKey, project.RootPath, isVault);
        }
        return true;
    }

    private static ProjectSummary ToSummary(Project p) => new(p.Id, p.Name, p.Category, p.Backend.ToString());

    // ── Brain note (Projects/[Name]) ────────────────────────────────────────────────
    // Deterministic, structural fields only (type/category/backend) — the descriptive summary is
    // Engram's job over time, same tending discipline as any other note, never overwritten here.

    private static void EnsureBrainNote(Project project)
    {
        if (!Brain.Ready) return;
        try
        {
            if (Brain.GetNote("Projects") is null)
                Brain.AddNote("Projects", "Hub for every project Ari knows about.", Array.Empty<string>(), type: "hub");
            Brain.AddNote($"Projects/{project.Name}", ProjectNoteBody(project), Array.Empty<string>(), type: "project");
            // The hub links DOWN to each direct child (GraphRulebook) — deterministic, idempotent,
            // safe to call even if other hubs in the vault also happen to be missing a member link.
            GraphMaintenance.EnsureHubChildLinks();
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to create brain note for project '{Name}'.", project.Name);
        }
    }

    private static void RenameBrainNote(string oldName, string newName)
    {
        if (!Brain.Ready) return;
        try
        {
            Note? existing = Brain.GetNote(oldName);
            if (existing is null) return; // no note to rename (e.g. brain wasn't ready at creation)
            Brain.EditNote(oldName, existing.Content, existing.Aliases, newName: $"Projects/{newName}", type: "project");
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to rename brain note '{Old}' -> '{New}'.", oldName, newName);
        }
    }

    private static string ProjectNoteBody(Project project) =>
        $"Category: {(project.Category.Length > 0 ? project.Category : "none")}\n" +
        $"Storage: {project.Backend}\n\n" +
        "(No summary yet — Ari will fill this in as we discuss the project.)\n\n" +
        "[[Projects]]\n";
}
