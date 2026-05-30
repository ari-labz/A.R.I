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
            await OnReady();
        }
        catch (InvalidOperationException ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain could not connect to Trilium: {Message}", ex.Message);
        }
    }

    private async Task OnReady()
    {
        triliumReady = true;
        List<string> titles = await trilium.GetAllNoteTitles();
        Common.Logger.LogInformation("Brain connected to Trilium. {Count} note(s) in graph.", titles.Count);
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

        // Deletes category folders — Trilium cascades to all child notes
        int count = await trilium.PurgeAllNotes();
        Common.Logger.LogInformation("Brain purged {Count} category folder(s) and all their notes.", count);
        return count;
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

            // The canonical name for the note we just saved — avoids a search that would miss it
            string savedName = !string.IsNullOrWhiteSpace(incoming.MergeWith) ? incoming.MergeWith : incoming.Name;

            Dictionary<string, string> resolvedLinkIds = new(StringComparer.OrdinalIgnoreCase);

            foreach (string linkName in linkedNames)
            {
                // If this link refers to the note we just saved, use its ID directly
                string? toId = string.Equals(linkName, savedName, StringComparison.OrdinalIgnoreCase)
                    ? noteId
                    : await trilium.FindNoteIdByTitleAnywhere(linkName);

                if (toId is null)
                {
                    ExtractedNote stub = new()
                    {
                        Category = NoteCategory.Unknown,
                        Name     = linkName,
                        Info     = new List<string> { "Mentioned in conversation. No further details yet." }
                    };
                    toId = await trilium.CreateNote(linkName, BuildNote(stub, null), NoteCategory.Unknown);
                    Common.Logger.LogInformation("created stub: {Name}", linkName);
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
                    Common.Logger.LogInformation("merged '{From}' into '{To}'.", incoming.Name, incoming.MergeWith);
                }
                return targetId;
            }
            else
            {
                string newId = await trilium.CreateNote(incoming.MergeWith, BuildNote(canonical, null), incoming.Category);
                Common.Logger.LogInformation("created note: {Name} (via mergeWith)", incoming.MergeWith);
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
                Common.Logger.LogInformation("updated note: {Name}", incoming.Name);
            }
            else
            {
                Common.Logger.LogInformation("skipped: {Name} (no changes)", incoming.Name);
            }
            return existingId;
        }
        else
        {
            string newId = await trilium.CreateNote(incoming.Name, BuildNote(incoming, null), incoming.Category);
            Common.Logger.LogInformation("created note: {Name}", incoming.Name);
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
