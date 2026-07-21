namespace ARI.Common;

/// <summary>Read-only view of a project, safe to hand across the ARI.LLM/ARI.API boundary without
/// either side depending on the other's concrete types.</summary>
public record ProjectSummary(string Id, string Name, string Type, string Category, string Backend);

/// <summary>
/// The contract ARI.LLM's project_tools use to create/find/bind projects, without ARI.LLM taking a
/// dependency on ARI.API (where Project/ProjectStore actually live — the wrong direction, since
/// ARI.API already depends on ARI.LLM). ARI.API provides the real implementation and registers it
/// via Modules.Register at startup; ARI.LLM only ever sees this interface.
/// </summary>
public interface IProjectService
{
    IReadOnlyList<ProjectSummary> List();

    /// <summary>type must be "Repository" or "ObsidianGraph" (case-insensitive); an unrecognised value
    /// falls back to Repository. category is free text. Backend defaults per type, same as the REST API.</summary>
    ProjectSummary? Create(string name, string type, string? category);

    bool Rename(string id, string newName);

    /// <summary>Binds a thread to a project: persists the thread→project mapping and, for a ServerFs
    /// project, immediately makes filesystem_tools/obsidian_tools resolve on that thread — the model
    /// doesn't have to wait for the next message to act on what it just bound.</summary>
    bool BindThread(string threadKey, string projectId);
}
