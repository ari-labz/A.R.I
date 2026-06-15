using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.Brain;

public class BrainService
{
    private readonly TriliumClient trilium;
    private readonly int brainCacheSize;
    private readonly string backupPath;
    private readonly int maxBackups;
    public string BrainPublicUrl { get; private set; } = string.Empty;
    private bool triliumReady = false;

    private Dictionary<string, string> noteIdCache   = new(StringComparer.OrdinalIgnoreCase); // title → noteId
    private readonly Dictionary<string, string> branchIdCache   = new(); // noteId → branchId
    private readonly Dictionary<string, string> noteFolderCache = new(); // noteId → folder path (e.g. "People")

    // Cached flat list of all note titles. Null = dirty, rebuilt on next GetNoteTitles() call.
    private List<string>? cachedTitles;

    // MRU content cache: keyed by note title (or "\x00markdown\x00{title}"). Front = most recently used, back = oldest.
    // Shared by all consumers — Recall, Engram, Refactor. No per-consumer caches needed.
    private readonly LinkedList<string> contentCacheOrder = new();
    private readonly Dictionary<string, string> contentCacheStore = new(StringComparer.OrdinalIgnoreCase);

    public BrainService(BrainConfig config, ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
            Common.InitialiseLogger(loggerFactory);

        trilium          = new TriliumClient(config.TriliumUrl, config.EtapiToken, config.RootNoteId);
        brainCacheSize   = config.BrainCacheSize;
        backupPath       = config.BackupPath;
        maxBackups       = config.MaxBackups;
        BrainPublicUrl   = config.BrainPublicUrl;
        _ = Startup();
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    private async Task Startup()
    {
        int[] retryDelaysSeconds = [2, 5, 10, 15, 30];
        foreach (int delay in retryDelaysSeconds)
        {
            try
            {
                await trilium.VerifyConnection();
                await OnReady();
                return;
            }
            catch (Exception ex)
            {
                Common.Logger.LogWarning("Brain could not connect to Trilium (retrying in {Delay}s): {Message}", delay, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }
        }

        // Final attempt with no further retry.
        try
        {
            await trilium.VerifyConnection();
            await OnReady();
        }
        catch (Exception ex)
        {
            triliumReady = false;
            Common.Logger.LogError("Brain failed to connect to Trilium after all retries: {Message}", ex.Message);
        }
    }

    private async Task OnReady()
    {
        triliumReady = true;
        Dictionary<string, (string Id, string FolderPath, string BranchId)> all = await trilium.GetAllNoteIds();
        noteIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string title, (string Id, string FolderPath, string BranchId) info) in all)
        {
            noteIdCache[title]          = info.Id;
            noteFolderCache[info.Id]    = info.FolderPath;
            if (!string.IsNullOrEmpty(info.BranchId))
                branchIdCache[info.Id]  = info.BranchId;
        }
        cachedTitles = null; // mark dirty so first call rebuilds from the new noteIdCache
        Common.Logger.LogInformation("Brain connected to Trilium. {Count} note(s) in graph.", noteIdCache.Count);
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures that <paramref name="targetName"/> contains a [[link]] back to <paramref name="sourceName"/>.
    /// If the link is already present, does nothing. Otherwise inserts it into an existing
    /// ## See Also section, or creates one immediately before ## Changelog, or appends one at the end.
    /// This is the programmatic backlink pass — no LLM call involved.
    /// </summary>
    public async Task AddBacklink(string targetName, string sourceName)
    {
        if (!triliumReady) return;
        if (string.Equals(targetName, sourceName, StringComparison.OrdinalIgnoreCase)) return;

        string? noteId = await FindNoteId(targetName);
        if (noteId is null) return;

        string? html = await trilium.GetNoteContent(noteId);
        if (html is null) return;

        string markdown = MarkdownConverter.FromHtml(html);

        // Already has the backlink — nothing to do.
        if (markdown.Contains($"[[{sourceName}]]", StringComparison.OrdinalIgnoreCase)) return;

        string link = $"- [[{sourceName}]]";
        string updated;

        if (Regex.IsMatch(markdown, @"^## See Also", RegexOptions.Multiline))
        {
            // Insert as first bullet under existing See Also heading.
            updated = Regex.Replace(markdown, @"(## See Also\r?\n)", $"$1{link}\n", RegexOptions.Multiline);
        }
        else if (Regex.IsMatch(markdown, @"^## Changelog", RegexOptions.Multiline))
        {
            // Insert a new See Also section before Changelog.
            updated = Regex.Replace(markdown, @"(## Changelog)", $"## See Also\n\n{link}\n\n## Changelog", RegexOptions.Multiline);
        }
        else
        {
            updated = markdown.TrimEnd() + $"\n\n## See Also\n\n{link}\n";
        }

        string updatedHtml = MarkdownConverter.ToHtml(updated);
        string resolved    = MarkdownConverter.ResolveLinks(updatedHtml, await ResolveLinkNames(updatedHtml));
        await trilium.UpdateNoteContent(noteId, resolved);

        string folder = noteFolderCache.TryGetValue(noteId, out string? f) ? f : string.Empty;
        UpdateContentCache(targetName, folder, resolved);
        Common.Logger.LogInformation("[Brain] Backlink [[{Source}]] added to '{Target}'", sourceName, targetName);
    }

    public async Task<List<string>> GetNoteTitles()
    {
        if (!triliumReady) await Startup();
        if (!triliumReady) return new List<string>();
        cachedTitles ??= noteIdCache.Keys.ToList();
        return cachedTitles;
    }

    /// <summary>
    /// Returns full paths for every note (e.g. "People/[REDACT]'s Family/Immediate Family/[REDACT]").
    /// Notes at the root level are returned as bare titles.
    /// </summary>
    public async Task<List<string>> GetNotePaths()
    {
        if (!triliumReady) await Startup();
        if (!triliumReady) return new List<string>();
        List<string> paths = new(noteIdCache.Count);
        foreach ((string title, string id) in noteIdCache)
        {
            string folder = noteFolderCache.TryGetValue(id, out string? f) ? f : string.Empty;
            paths.Add(string.IsNullOrEmpty(folder) ? title : $"{folder}/{title}");
        }
        return paths;
    }

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

    /// <summary>Returns the Trilium note ID for the given title, or null if not found.</summary>
    public Task<string?> GetNoteId(string title) => FindNoteId(title);

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
        string path     = string.IsNullOrEmpty(folder) ? noteName : $"{folder}/{noteName}";
        string result   = $"Path: {path}\n\n{markdown}";
        AddContentToCache(cacheKey, result);
        return result;
    }

    public async Task DeleteNote(string title)
    {
        if (!triliumReady) return;

        // Support path-qualified names like "Unknown/Recall".
        // When a path is given, find the note by bare title but verify it lives in the expected folder.
        // This prevents accidentally deleting a same-titled note in a different folder
        // (e.g. deleting Projects/ARI/Agents/Recall when trying to delete Unknown/Recall).
        string? noteId = null;
        if (title.Contains('/'))
        {
            string bareTitle      = title[(title.LastIndexOf('/') + 1)..];
            string expectedFolder = title[..title.LastIndexOf('/')];

            if (noteIdCache.TryGetValue(bareTitle, out string? candidate))
            {
                string actualFolder = noteFolderCache.TryGetValue(candidate, out string? f) ? f : string.Empty;
                if (string.Equals(actualFolder, expectedFolder, StringComparison.OrdinalIgnoreCase))
                    noteId = candidate;
            }

            if (noteId is null)
            {
                // Fallback: search Trilium — only use result if it's in the expected folder.
                string? found = await trilium.FindNoteIdByTitleAnywhere(bareTitle);
                if (found is not null)
                {
                    string actualFolder = noteFolderCache.TryGetValue(found, out string? f) ? f : string.Empty;
                    if (string.Equals(actualFolder, expectedFolder, StringComparison.OrdinalIgnoreCase))
                        noteId = found;
                }
            }
        }
        else
        {
            noteId = await FindNoteId(title);
        }

        if (noteId is null)
        {
            Common.Logger.LogWarning("[Brain] Delete failed — '{Title}' not found.", title);
            return;
        }
        bool deleted = await trilium.DeleteNote(noteId);
        if (!deleted)
        {
            Common.Logger.LogWarning("[Brain] Delete of '{Title}' skipped — note still has children (moved notes may not have fully detached yet).", title);
            return;
        }
        noteIdCache.Remove(title);
        branchIdCache.Remove(noteId);
        noteFolderCache.Remove(noteId);
        cachedTitles = null;
        InvalidateContentCache(title);
        Common.Logger.LogInformation("deleted: {Title}", title);
    }

    public async Task<string> Backup()
    {
        if (!triliumReady) return "Brain is not connected — backup aborted.";

        // Collect every note: title, folder, and markdown content.
        List<object> notes = new();
        foreach (string title in noteIdCache.Keys.ToList())
        {
            string? content = await GetNote(title);
            if (content is null) continue;
            string folder = string.Empty;
            if (noteIdCache.TryGetValue(title, out string? id) && noteFolderCache.TryGetValue(id, out string? f))
                folder = f;
            notes.Add(new { title, folder, content });
        }

        object payload = new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
            noteCount = notes.Count,
            notes
        };

        string json     = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        string dirPath  = Path.GetFullPath(backupPath);
        Directory.CreateDirectory(dirPath);

        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        string zipPath   = Path.Combine(dirPath, $"ARI-Brain-{timestamp}.zip");

        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("brain.json", CompressionLevel.Optimal);
            using StreamWriter writer = new(entry.Open());
            await writer.WriteAsync(json);
        }

        // Enforce max backup limit — delete oldest files over the limit.
        FileInfo[] existing = new DirectoryInfo(dirPath)
            .GetFiles("ARI-Brain-*.zip")
            .OrderBy(f => f.CreationTimeUtc)
            .ToArray();

        int deleted = 0;
        while (existing.Length - deleted > maxBackups)
        {
            existing[deleted].Delete();
            Common.Logger.LogInformation("[Brain] Deleted old backup: {Name}", existing[deleted].Name);
            deleted++;
        }

        Common.Logger.LogInformation("[Brain] Backup saved: {Path} ({Count} note(s))", zipPath, notes.Count);
        return $"Backup saved — {notes.Count} note(s). File: `{Path.GetFileName(zipPath)}`";
    }

    public async Task<int> PurgeAllNotes()
    {
        if (!triliumReady) return 0;

        List<string> noteIds = noteIdCache.Values.ToList();
        Common.Logger.LogInformation("Brain purge requested — {Count} note(s) to delete.", noteIds.Count);

        int deleted = 0;
        foreach (string noteId in noteIds)
        {
            try { if (await trilium.DeleteNote(noteId)) deleted++; }
            catch (Exception ex) { Common.Logger.LogWarning("Failed to delete note {NoteId}: {Message}", noteId, ex.Message); }
        }

        try { await trilium.PurgeCategoryFolders(); }
        catch (Exception ex) { Common.Logger.LogWarning("Failed to remove folders: {Message}", ex.Message); }

        noteIdCache.Clear();
        branchIdCache.Clear();
        noteFolderCache.Clear();
        cachedTitles = null;
        contentCacheOrder.Clear();
        contentCacheStore.Clear();
        Common.Logger.LogInformation("Brain purged — {Deleted}/{Total} note(s) deleted.", deleted, noteIds.Count);
        return deleted;
    }

    // ── Dirty set ─────────────────────────────────────────────────────────────────

    private const string DirtyLabel = "ariDirty";

    /// <summary>
    /// Marks notes as dirty by attaching an #ariDirty label in Trilium.
    /// Called by Engram after every write so Refactor knows what changed.
    /// Already-dirty notes are skipped (no duplicate labels created).
    /// </summary>
    public async Task MarkDirty(IEnumerable<string> titles)
    {
        if (!triliumReady) return;
        foreach (string title in titles)
        {
            string? noteId = await FindNoteId(title);
            if (noteId is null) continue;

            // Skip if already marked
            List<(string AttributeId, string Type, string Name, string Value)> attrs = await trilium.GetNoteAttributes(noteId);
            if (attrs.Any(a => a.Type == "label" && a.Name == DirtyLabel)) continue;

            try { await trilium.CreateLabelAttribute(noteId, DirtyLabel); }
            catch (Exception ex) { Common.Logger.LogWarning("[Brain] MarkDirty failed for '{Title}': {Msg}", title, ex.Message); }
        }
    }

    /// <summary>Returns the bare titles of all notes currently marked #ariDirty.</summary>
    public async Task<List<string>> GetDirtyNotes()
    {
        if (!triliumReady) return new();
        List<string> noteIds = await trilium.SearchNoteIdsByLabel(DirtyLabel);
        List<string> titles = new();
        foreach (string id in noteIds)
        {
            string? title = noteIdCache.FirstOrDefault(kv => kv.Value == id).Key;
            if (title is not null) titles.Add(title);
        }
        return titles;
    }

    /// <summary>Removes the #ariDirty label from the given notes after a successful refactor pass.</summary>
    public async Task ClearDirty(IEnumerable<string> titles)
    {
        if (!triliumReady) return;
        foreach (string title in titles)
        {
            string? noteId = await FindNoteId(title);
            if (noteId is null) continue;

            List<(string, string, string, string)> attrs = await trilium.GetNoteAttributes(noteId);
            foreach ((string attrId, string type, string name, string _) in attrs)
                if (type == "label" && name == DirtyLabel)
                    try { await trilium.DeleteAttribute(attrId); }
                    catch (Exception ex) { Common.Logger.LogWarning("[Brain] ClearDirty failed for '{Title}': {Msg}", title, ex.Message); }
        }
    }

    /// <summary>
    /// Returns bare titles of all notes whose top-level folder matches <paramref name="folderPath"/>.
    /// Pure in-memory lookup — no network calls.
    /// Pass an empty string to get root-level notes (hub notes with no folder).
    /// </summary>
    public List<string> GetTitlesByFolder(string folderPath)
        => noteIdCache
            .Where(kv => noteFolderCache.TryGetValue(kv.Value, out string? f)
                         && string.Equals(f, folderPath, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

    public async Task<List<string>> SearchNote(string searchTerm)
    {
        if (!triliumReady) return new List<string>();
        return await trilium.SearchNotes(searchTerm);
    }

    /// <summary>Returns the bare titles of all notes linked from the given note via [[Name]] syntax.</summary>
    public async Task<List<string>> GetNoteLinks(string title)
    {
        string? markdown = await GetNote(title);
        if (markdown is null) return new List<string>();
        return Regex.Matches(markdown, @"\[\[([^\]]+)\]\]")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes Unknown/ stubs that are duplicates of properly-categorised notes.
    /// These are identified at startup and tracked until cleaned up.
    /// Returns the number of stubs deleted.
    /// </summary>
    public async Task<int> CleanUnknownStubs()
    {
        if (!triliumReady) return 0;
        int deleted = await trilium.DeleteSuppressedStubs();
        if (deleted > 0)
        {
            Common.Logger.LogInformation("[Brain] Cleaned {Count} duplicate Unknown stub(s).", deleted);
            cachedTitles = null;
        }
        return deleted;
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
                    cachedTitles          = null;
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
            string[] folders     = FolderPath(add.NoteName);
            string targetFolder  = string.Join("/", folders);
            string currentFolder = noteFolderCache.TryGetValue(existingId, out string? cf) ? cf : string.Empty;

            await trilium.UpdateNoteContent(existingId, resolved);
            if (add.Aliases.Count > 0)
                await trilium.SetAliasLabels(existingId, add.Aliases);

            bool moved = false;
            if (!string.Equals(currentFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (!branchIdCache.TryGetValue(existingId, out string? branchId))
                    branchId = await trilium.GetPrimaryBranchId(existingId);
                if (!string.IsNullOrEmpty(branchId))
                {
                    string newBranchId = await trilium.MoveNoteToFolderPath(branchId, existingId, folders);
                    branchIdCache[existingId] = newBranchId;
                    moved = true;
                    Common.Logger.LogInformation("added (moved): {From} → {To}", $"{currentFolder}/{name}", add.NoteName);
                }
                else
                {
                    Common.Logger.LogWarning("[Brain] Move skipped — could not resolve branch for '{Name}'.", add.NoteName);
                }
            }

            // Only update the folder cache if the move actually succeeded.
            // Updating on failure would desync the cache from Trilium's actual tree.
            if (moved || string.Equals(currentFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
                noteFolderCache[existingId] = targetFolder;
            UpdateContentCache(name, targetFolder, resolved);
            Common.Logger.LogInformation("added (updated): {Name}", add.NoteName);
        }
        else
        {
            string[] folders = FolderPath(add.NoteName);
            (string id, string branchId) = await trilium.CreateNoteAtPath(folders, name, resolved);
            noteIdCache[name]   = id;
            branchIdCache[id]   = branchId;
            noteFolderCache[id] = string.Join("/", folders);
            if (add.Aliases.Count > 0)
                await trilium.SetAliasLabels(id, add.Aliases);
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
        if (edit.Aliases.Count > 0)
            await trilium.SetAliasLabels(noteId, edit.Aliases);
        string currentFolder = noteFolderCache.TryGetValue(noteId, out string? cf) ? cf : "Unknown";
        UpdateContentCache(currentName, currentFolder, resolved);
        Common.Logger.LogInformation("edited: {Name}", currentName);

        if (string.IsNullOrWhiteSpace(edit.NewNoteName)) return;

        string newName      = NoteName(edit.NewNoteName);
        string[] newFolders = FolderPath(edit.NewNoteName);

        if (!branchIdCache.TryGetValue(noteId, out string? branchId))
            branchId = await trilium.GetPrimaryBranchId(noteId);

        bool moved = false;
        if (!string.IsNullOrEmpty(branchId))
        {
            string newBranchId = await trilium.MoveNoteToFolderPath(branchId, noteId, newFolders);
            branchIdCache[noteId]   = newBranchId;
            noteFolderCache[noteId] = string.Join("/", newFolders);
            moved = true;
        }
        else
        {
            Common.Logger.LogWarning("[Brain] Move skipped — could not resolve branch for '{Name}'.", edit.NoteName);
        }

        bool renamed = !string.Equals(currentName, newName, StringComparison.OrdinalIgnoreCase);
        if (renamed)
        {
            await trilium.RenameNote(noteId, newName);
            noteIdCache.Remove(currentName);
            noteIdCache[newName] = noteId;
            cachedTitles = null;
            InvalidateContentCache(currentName);
        }

        if (moved && renamed)
            Common.Logger.LogInformation("moved+renamed: {From} → {To}", edit.NoteName, edit.NewNoteName);
        else if (moved)
            Common.Logger.LogInformation("moved: {From} → {To}", edit.NoteName, edit.NewNoteName);
        else if (renamed)
            Common.Logger.LogInformation("renamed: {From} → {To}", edit.NoteName, edit.NewNoteName);
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
        cachedTitles           = null;
        Common.Logger.LogInformation("created stub: {Name}", name);
        return newId;
    }

    // ── Content cache ─────────────────────────────────────────────────────────────

    private bool TryGetCachedContent(string key, out string? content)
    {
        if (brainCacheSize <= 0 || !contentCacheStore.TryGetValue(key, out content))
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
        if (brainCacheSize <= 0) return;

        if (contentCacheStore.ContainsKey(key))
        {
            contentCacheOrder.Remove(key);
        }
        else if (contentCacheStore.Count >= brainCacheSize)
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

    /// <summary>
    /// Returns the folder path (all segments except the last).
    /// "People/[REDACT]" → ["People"]
    /// "People"       → []   (root level — GetOrCreateFolderPath([]) returns rootNoteId)
    /// Only EnsureNoteExists explicitly uses ["Unknown"] for truly uncategorised stubs.
    /// </summary>
    private static string[] FolderPath(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[..^1] : [];
    }
}
