using ARI.Brain;
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

    public ProjectSummary? Create(string name, string type, string? category, string? backend = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!Enum.TryParse(type, ignoreCase: true, out ProjectType parsedType)) parsedType = ProjectType.Repository;

        string id = Guid.NewGuid().ToString("N");
        // An explicit backend wins; otherwise both types default to ServerFs — the server manages the
        // project folder so web-panel threads can use filesystem tools without a local Electron bridge.
        // Desktop app (Electron) threads that need a local disk path will pass LocalPath explicitly,
        // which overrides effectiveLocalPath regardless of backend. RemoteFs is still selectable via
        // the explicit backend param when the caller needs it (legacy desktop-only repos).
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
            Type:         parsedType,
            Category:     category?.Trim() ?? "",
            Backend:      resolvedBackend,
            RootPath:     rootPath);

        store.Add(project);
        EnsureBrainNote(project);
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
            bool isServerFsVault = project is { Type: ProjectType.ObsidianGraph, Backend: StorageBackend.ServerFs };
            llm.BindProjectContext(threadKey, project.RootPath, isServerFsVault);
        }
        return true;
    }

    private static ProjectSummary ToSummary(Project p) => new(p.Id, p.Name, p.Type.ToString(), p.Category, p.Backend.ToString());

    // ── Brain note (Projects/[Name]) ────────────────────────────────────────────────
    // Deterministic, structural fields only (type/category/backend) — the descriptive summary is
    // Engram's job over time, same tending discipline as any other note, never overwritten here.

    private static void EnsureBrainNote(Project project)
    {
        if (!BrainModule.Ready) return;
        try
        {
            List<EngramAdd> adds = new();
            if (BrainModule.GetNote("Projects") is null)
                adds.Add(new EngramAdd { NoteName = "Projects", Content = "Hub for every project Ari knows about.", Type = "hub" });
            adds.Add(new EngramAdd
            {
                NoteName = $"Projects/{project.Name}",
                Content  = ProjectNoteBody(project),
                Type     = "project",
            });
            BrainModule.AddNotes(adds);
            // The hub links DOWN to each direct child (GraphRulebook) — deterministic, idempotent,
            // safe to call even if other hubs in the vault also happen to be missing a member link.
            BrainModule.EnsureHubChildLinks();
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to create brain note for project '{Name}'.", project.Name);
        }
    }

    private static void RenameBrainNote(string oldName, string newName)
    {
        if (!BrainModule.Ready) return;
        try
        {
            Note? existing = BrainModule.GetNote(oldName);
            if (existing is null) return; // no note to rename (e.g. brain wasn't ready at creation)
            BrainModule.EditNotes(new[]
            {
                new EngramEdit
                {
                    NoteName    = oldName,
                    NewNoteName = $"Projects/{newName}",
                    Content     = existing.Content,
                    Aliases     = existing.Aliases,
                    Type        = "project",
                }
            });
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning(ex, "[Projects] Failed to rename brain note '{Old}' -> '{New}'.", oldName, newName);
        }
    }

    private static string ProjectNoteBody(Project project) =>
        $"Type: {project.Type}\n" +
        $"Category: {(project.Category.Length > 0 ? project.Category : "none")}\n" +
        $"Storage: {project.Backend}\n\n" +
        "(No summary yet — Ari will fill this in as we discuss the project.)\n\n" +
        "[[Projects]]\n";
}
