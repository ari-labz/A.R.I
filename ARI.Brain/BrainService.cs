using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.Brain;

public class BrainService
{
    private readonly TriliumClient trilium;
    private bool triliumReady = false;

    public BrainService(string configPath, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        BrainConfig config = BrainConfig.LoadFrom(configPath);
        trilium = new TriliumClient(config.TriliumUrl, config.EtapiToken, config.RootNoteId);

        _ = Startup();
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    private async Task Startup()
    {
        try
        {
            await trilium.VerifyConnection();
            triliumReady = true;
            Common.Logger.LogInformation("Brain connected to Trilium.");
        }
        catch (InvalidOperationException ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain could not connect to Trilium: {Message}", ex.Message);
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    // TODO: optimise — currently returns all note titles. Future: accept search terms and return only relevant notes.
    public async Task<List<string>> GetNoteTitles()
    {
        if (!triliumReady) return new List<string>();
        return await trilium.GetAllNoteTitles();
    }

    public async Task<string?> GetNoteContent(string title)
    {
        if (!triliumReady) return null;
        string? noteId = await trilium.FindNoteIdByTitleAnywhere(title);
        if (noteId is null) return null;
        return await trilium.GetNoteContent(noteId);
    }

    public async Task<int> PurgeAllNotes()
    {
        if (!triliumReady) return 0;

        List<string> ids = await trilium.GetAllNoteIds();
        foreach (string id in ids)
            await trilium.DeleteNote(id);

        trilium.ClearCache();
        Common.Logger.LogInformation("Brain purged {Count} notes.", ids.Count);
        return ids.Count;
    }

    public async Task<List<string>> SearchNote(string searchTerm)
    {
        if (!triliumReady) return new List<string>();
        return await trilium.SearchNotes(searchTerm);
    }

    public async Task SaveNote(ExtractedNote incoming)
    {
        if (!triliumReady)
        {
            await Startup();
            if (!triliumReady) return;
        }

        try
        {
            string? noteId = await SaveNoteInternal(incoming);
            if (noteId is null) return;

            string? currentContent = await trilium.GetNoteContent(noteId);
            if (currentContent is null) return;

            List<string> linkedNames = ExtractLinkPlaceholders(currentContent);
            if (linkedNames.Count == 0) return;

            Dictionary<string, string> resolvedLinkIds = new(StringComparer.OrdinalIgnoreCase);

            foreach (string linkName in linkedNames)
            {
                string? toId = await trilium.FindNoteIdByTitleAnywhere(linkName);

                if (toId is null)
                {
                    ExtractedNote stub = new()
                    {
                        Category = NoteCategory.Unknown,
                        Name     = linkName,
                        Info     = new List<string> { "Mentioned in conversation. No further details yet." }
                    };
                    toId = await trilium.CreateNote(linkName, BuildNote(stub, null), NoteCategory.Unknown);
                    Common.Logger.LogInformation("Brain created stub note: {Name}", linkName);
                }

                resolvedLinkIds[linkName] = toId;
                await trilium.CreateRelation(noteId, toId);
            }

            string resolved = NoteBuilder.ResolveLinks(currentContent, resolvedLinkIds);
            if (resolved != currentContent)
                await trilium.UpdateNoteContent(noteId, resolved);
        }
        catch (Exception ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain failed to save note '{Name}': {Message}", incoming.Name, ex.Message);
        }
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private async Task<string?> SaveNoteInternal(ExtractedNote incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.MergeWith))
        {
            string? targetId = await trilium.FindNoteIdByTitleAnywhere(incoming.MergeWith);
            ExtractedNote canonical = new()
            {
                Category     = incoming.Category,
                Name         = incoming.MergeWith,
                Aliases      = incoming.Aliases.Contains(incoming.Name) ? incoming.Aliases : [incoming.Name, ..incoming.Aliases],
                Pronouns     = incoming.Pronouns,
                Relation     = incoming.Relation,
                Events       = incoming.Events,
                Info         = incoming.Info,
                Feelings     = incoming.Feelings,
                Observations = incoming.Observations,
                Date         = incoming.Date
            };

            if (targetId is not null)
            {
                string? existing = await trilium.GetNoteContent(targetId);
                string merged = BuildNote(canonical, existing);
                if (merged.Trim() != existing?.Trim())
                {
                    await trilium.UpdateNoteContent(targetId, merged);
                    Common.Logger.LogInformation("Brain merged '{From}' into '{To}'.", incoming.Name, incoming.MergeWith);
                }
                return targetId;
            }
            else
            {
                string newId = await trilium.CreateNote(incoming.MergeWith, BuildNote(canonical, null), incoming.Category);
                Common.Logger.LogInformation("Brain created note (via mergeWith): {Name}", incoming.MergeWith);
                return newId;
            }
        }

        string? existingId = await trilium.FindNoteIdByTitleAnywhere(incoming.Name);

        if (existingId is not null)
        {
            string? existing = await trilium.GetNoteContent(existingId);
            string mergedHtml = BuildNote(incoming, existing);

            if (mergedHtml.Trim() != existing?.Trim())
            {
                await trilium.UpdateNoteContent(existingId, mergedHtml);
                Common.Logger.LogInformation("Brain updated note: {Name} [{Category}]", incoming.Name, incoming.Category);
            }
            else
            {
                Common.Logger.LogInformation("Brain skipped note (no changes): {Name}", incoming.Name);
            }
            return existingId;
        }
        else
        {
            string newId = await trilium.CreateNote(incoming.Name, BuildNote(incoming, null), incoming.Category);
            Common.Logger.LogInformation("Brain created note: {Name} [{Category}]", incoming.Name, incoming.Category);
            return newId;
        }
    }

    private static List<string> ExtractLinkPlaceholders(string html)
    {
        MatchCollection matches = Regex.Matches(html, @"\{\{LINK:([^}]+)\}\}");
        return matches.Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildNote(ExtractedNote incoming, string? existing) => incoming.Category switch
    {
        NoteCategory.People => NoteBuilder.BuildOrMergePerson(incoming, existing),
        NoteCategory.Events => NoteBuilder.BuildOrMergeEvent(incoming, existing),
        _                   => NoteBuilder.BuildOrMergeGeneric(incoming, existing)
    };
}
