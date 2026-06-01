using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.Brain;

public class BrainService
{
    private readonly TriliumClient trilium;
    private readonly int contentCacheSize;
    private bool triliumReady = false;

    private Dictionary<string, string> noteIdCache   = new(StringComparer.OrdinalIgnoreCase); // title → noteId
    private readonly Dictionary<string, string> branchIdCache   = new(); // noteId → branchId
    private readonly Dictionary<string, string> noteFolderCache = new(); // noteId → folder path (e.g. "People")

    // MRU content cache: keyed by note title. Front = most recently used, back = oldest.
    private readonly LinkedList<string> contentCacheOrder = new();
    private readonly Dictionary<string, string> contentCacheStore = new(StringComparer.OrdinalIgnoreCase);

    public BrainService(string configPath, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        BrainConfig config = BrainConfig.LoadFrom(configPath);
        trilium          = new TriliumClient(config.TriliumUrl, config.EtapiToken, config.RootNoteId);
        contentCacheSize = config.ContentCacheSize;
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
        Dictionary<string, (string Id, string FolderPath)> all = await trilium.GetAllNoteIds();
        noteIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, info) in all)
        {
            noteIdCache[title]          = info.Id;
            noteFolderCache[info.Id]    = info.FolderPath;
        }
        Common.Logger.LogInformation("Brain connected to Trilium. {Count} note(s) in graph.", noteIdCache.Count);
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    public Task<List<string>> GetNoteTitles()
        => Task.FromResult(triliumReady ? noteIdCache.Keys.ToList() : new List<string>());

    public async Task<string?> GetNoteContent(string title)
    {
        if (!triliumReady) return null;
        if (TryGetCachedContent(title, out string? cached)) return cached;
        string? noteId = await FindNoteId(title);
        if (noteId is null) return null;
        string? content = await trilium.GetNoteContent(noteId);
        if (content is not null) AddContentToCache(title, content);
        return content;
    }

    /// <summary>Returns the note as markdown (with [[Name]] links) for recursive fetch steps.</summary>
    public async Task<string?> GetNote(string noteName)
    {
        if (!triliumReady) return null;

        string cacheKey = $"\x00markdown\x00{noteName}";
        if (TryGetCachedContent(cacheKey, out string? cached)) return cached;

        string? noteId = await FindNoteId(noteName);
        if (noteId is null) return null;
        string? html = await trilium.GetNoteContent(noteId);
        if (html is null) return null;

        string folder   = noteFolderCache.TryGetValue(noteId, out string? f) ? f : "Unknown";
        string markdown = MarkdownConverter.FromHtml(html);
        string result   = $"Path: {folder}/{noteName}\n\n{markdown}";
        AddContentToCache(cacheKey, result);
        return result;
    }

    public async Task<int> PurgeAllNotes()
    {
        if (!triliumReady) return 0;

        List<string> noteIds = noteIdCache.Values.ToList();
        Common.Logger.LogInformation("Brain purge requested — {Count} note(s) to delete.", noteIds.Count);

        int deleted = 0;
        foreach (string noteId in noteIds)
        {
            try { await trilium.DeleteNote(noteId); deleted++; }
            catch (Exception ex) { Common.Logger.LogWarning("Failed to delete note {NoteId}: {Message}", noteId, ex.Message); }
        }

        try { await trilium.PurgeCategoryFolders(); }
        catch (Exception ex) { Common.Logger.LogWarning("Failed to remove folders: {Message}", ex.Message); }

        noteIdCache.Clear();
        branchIdCache.Clear();
        noteFolderCache.Clear();
        contentCacheOrder.Clear();
        contentCacheStore.Clear();
        Common.Logger.LogInformation("Brain purged — {Deleted}/{Total} note(s) deleted.", deleted, noteIds.Count);
        return deleted;
    }

    public async Task<List<string>> SearchNote(string searchTerm)
    {
        if (!triliumReady) return new List<string>();
        return await trilium.SearchNotes(searchTerm);
    }

    /// <summary>
    /// Creates new notes. Pre-registers all note names before link resolution so
    /// cross-batch [[links]] resolve correctly without creating Unknown stubs.
    /// </summary>
    public async Task AddNotes(IReadOnlyList<EngramAdd> adds)
    {
        if (!triliumReady) { await Startup(); if (!triliumReady) return; }
        try
        {
            // Pre-pass: register all new note names in the correct folders.
            foreach (EngramAdd add in adds)
            {
                string name = NoteName(add.NoteName);
                if (!noteIdCache.ContainsKey(name))
                {
                    string[] folders = FolderPath(add.NoteName);
                    (string id, string branchId) = await trilium.CreateNoteAtPath(folders, name, string.Empty);
                    noteIdCache[name]     = id;
                    branchIdCache[id]     = branchId;
                    noteFolderCache[id]   = string.Join("/", folders);
                }
            }

            // Main pass: fill in content with links resolved.
            foreach (EngramAdd add in adds)
                await SaveAdd(add);
        }
        catch (Exception ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain.AddNotes failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Replaces existing notes with Engram's corrected content. Optionally moves them.
    /// Engram must have fetched the note in the fetch step before editing.
    /// </summary>
    public async Task EditNotes(IReadOnlyList<EngramEdit> edits)
    {
        if (!triliumReady) { await Startup(); if (!triliumReady) return; }
        try
        {
            foreach (EngramEdit edit in edits)
                await SaveEdit(edit);
        }
        catch (Exception ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain.EditNotes failed: {Message}", ex.Message);
        }
    }

    // ── Private ──────────────────────────────────────────────────────────────────

    private async Task SaveAdd(EngramAdd add)
    {
        string name    = NoteName(add.NoteName);
        string html    = MarkdownConverter.ToHtml(add.Content);
        string resolved = MarkdownConverter.ResolveLinks(html, await ResolveLinkNames(html));

        string? existingId = await FindNoteId(name);
        if (existingId is not null)
        {
            string[] folders = FolderPath(add.NoteName);
            await trilium.UpdateNoteContent(existingId, resolved);
            noteFolderCache[existingId] = string.Join("/", folders);
            UpdateContentCache(name, string.Join("/", folders), resolved);
            Common.Logger.LogInformation("added (updated): {Name}", add.NoteName);
        }
        else
        {
            string[] folders = FolderPath(add.NoteName);
            (string id, string branchId) = await trilium.CreateNoteAtPath(folders, name, resolved);
            noteIdCache[name]   = id;
            branchIdCache[id]   = branchId;
            noteFolderCache[id] = string.Join("/", folders);
            UpdateContentCache(name, string.Join("/", folders), resolved);
            Common.Logger.LogInformation("added: {Name}", add.NoteName);
        }
    }

    private async Task SaveEdit(EngramEdit edit)
    {
        string currentName = NoteName(edit.NoteName);
        string? noteId     = await FindNoteId(currentName);

        if (noteId is null)
        {
            Common.Logger.LogWarning("[Brain] Edit failed — '{Name}' not found.", currentName);
            return;
        }

        string html     = MarkdownConverter.ToHtml(edit.Content);
        string resolved = MarkdownConverter.ResolveLinks(html, await ResolveLinkNames(html));
        await trilium.UpdateNoteContent(noteId, resolved);
        string currentFolder = noteFolderCache.TryGetValue(noteId, out string? cf) ? cf : "Unknown";
        UpdateContentCache(currentName, currentFolder, resolved);
        Common.Logger.LogInformation("edited: {Name}", currentName);

        if (string.IsNullOrWhiteSpace(edit.NewNoteName)) return;

        string newName    = NoteName(edit.NewNoteName);
        string[] newFolders = FolderPath(edit.NewNoteName);

        if (!branchIdCache.TryGetValue(noteId, out string? branchId))
            branchId = await trilium.GetPrimaryBranchId(noteId);

        if (!string.IsNullOrEmpty(branchId))
        {
            string newBranchId = await trilium.MoveNoteToFolderPath(branchId, noteId, newFolders);
            branchIdCache[noteId] = newBranchId;
            noteFolderCache[noteId] = string.Join("/", newFolders);
        }

        if (!string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase))
        {
            await trilium.RenameNote(noteId, newName);
            noteIdCache.Remove(currentName);
            noteIdCache[newName] = noteId;
            // Old name entries are now stale; new name will be populated on next read.
            InvalidateContentCache(currentName);
            Common.Logger.LogInformation("moved+renamed: {From} → {To}", edit.NoteName, edit.NewNoteName);
        }
        else
        {
            Common.Logger.LogInformation("moved: {From} → {To}", edit.NoteName, edit.NewNoteName);
        }
    }

    /// <summary>Finds note IDs for all [[Name]] placeholders in html, creating Unknown stubs if needed.</summary>
    private async Task<Dictionary<string, string>> ResolveLinkNames(string html)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, @"\{\{LINK:([^}]+)\}\}"))
            names.Add(m.Groups[1].Value);

        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
            ids[name] = await EnsureNoteExists(name);
        return ids;
    }

    private async Task<string?> FindNoteId(string title)
    {
        if (noteIdCache.TryGetValue(title, out string? cached)) return cached;
        string? found = await trilium.FindNoteIdByTitleAnywhere(title);
        if (found is not null) noteIdCache[title] = found;
        return found;
    }

    private async Task<string> EnsureNoteExists(string name)
    {
        // Strip any folder prefix — [[Hardware/MacBook Studio M3 Ultra]] should resolve to "MacBook Studio M3 Ultra".
        string normalizedName = NoteName(name);
        string? id = await FindNoteId(normalizedName);
        if (id is not null) return id;
        name = normalizedName;

        string stubHtml = MarkdownConverter.ToHtml(
            $"# {name}\n\nMentioned in conversation. No further details yet.");

        (string newId, string branchId) = await trilium.CreateNoteAtPath(["Unknown"], name, stubHtml);
        noteIdCache[name]      = newId;
        branchIdCache[newId]   = branchId;
        noteFolderCache[newId] = "Unknown";
        Common.Logger.LogInformation("created stub: {Name}", name);
        return newId;
    }

    // ── Content cache ─────────────────────────────────────────────────────────────

    private bool TryGetCachedContent(string key, out string? content)
    {
        if (contentCacheSize <= 0 || !contentCacheStore.TryGetValue(key, out content))
        {
            content = null;
            return false;
        }

        contentCacheOrder.Remove(key);
        contentCacheOrder.AddFirst(key);
        return true;
    }

    private void AddContentToCache(string key, string content)
    {
        if (contentCacheSize <= 0) return;

        if (contentCacheStore.ContainsKey(key))
        {
            contentCacheOrder.Remove(key);
        }
        else if (contentCacheStore.Count >= contentCacheSize)
        {
            string oldest = contentCacheOrder.Last!.Value;
            contentCacheOrder.RemoveLast();
            contentCacheStore.Remove(oldest);
        }

        contentCacheStore[key] = content;
        contentCacheOrder.AddFirst(key);
    }

    private void UpdateContentCache(string name, string folder, string resolvedHtml)
    {
        string markdown = MarkdownConverter.FromHtml(resolvedHtml);
        AddContentToCache(name, resolvedHtml);
        AddContentToCache($"\x00markdown\x00{name}", $"Path: {folder}/{name}\n\n{markdown}");
    }

    private void InvalidateContentCache(string name)
    {
        if (contentCacheStore.Remove(name))
            contentCacheOrder.Remove(name);

        string markdownKey = $"\x00markdown\x00{name}";
        if (contentCacheStore.Remove(markdownKey))
            contentCacheOrder.Remove(markdownKey);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────────

    /// <summary>Returns the note title (last path segment). "People/[REDACT]" → "[REDACT]"</summary>
    private static string NoteName(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : path;
    }

    /// <summary>Returns the folder path (all segments except the last). "People/[REDACT]" → ["People"]</summary>
    private static string[] FolderPath(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[..^1] : ["Unknown"];
    }
}
