using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ARI.Brain;

public class TriliumClient
{
    private readonly HttpClient http;
    private readonly string rootNoteId;

    // Full folder path → noteId.  Keys: "People", "People/Family", etc.
    private readonly Dictionary<string, string> folderCache = new(StringComparer.OrdinalIgnoreCase);

    // IDs of Unknown/ stubs that were suppressed during GetAllNoteIds because a non-Unknown
    // note with the same title exists. Populated on each call to GetAllNoteIds.
    private readonly List<string> suppressedStubIds = new();

    public TriliumClient(string baseUrl, string token, string rootNoteId)
    {
        this.rootNoteId = rootNoteId;
        http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Public API ──────────────────────────────────────────────────────────────

    public async Task VerifyConnection()
    {
        const int maxAttempts = 10;
        const int delayMs = 2000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                HttpResponseMessage res = await http.GetAsync("etapi/app-info");

                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        "Trilium rejected the ETAPI token. Generate a new one in Trilium → Options → ETAPI and update AriBrain.json.");

                if (res.IsSuccessStatusCode) return;

                throw new InvalidOperationException(
                    $"Trilium returned an unexpected status: {(int)res.StatusCode} {res.StatusCode}.");
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(delayMs);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach Trilium at {http.BaseAddress} after {maxAttempts} attempts. ({ex.Message})", ex);
            }
        }
    }

    public async Task<string?> FindNoteIdByTitleAnywhere(string title)
    {
        foreach (string query in new[] { $"\"{title}\"", title })
        {
            string encoded = Uri.EscapeDataString(query);
            HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
            if (!res.IsSuccessStatusCode) continue;

            JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
            foreach (JsonNode? item in results)
            {
                if (item is null) continue;
                string? foundTitle = item["title"]?.GetValue<string>();
                string? foundId    = item["noteId"]?.GetValue<string>();
                if (string.Equals(foundTitle, title, StringComparison.OrdinalIgnoreCase) && foundId is not null)
                    return foundId;
            }
        }
        return null;
    }

    public async Task<string?> GetNoteContent(string noteId)
    {
        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}/content");
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsStringAsync();
    }

    /// <summary>Creates a note at the specified folder path, creating intermediate folders as needed.</summary>
    public async Task<(string NoteId, string BranchId)> CreateNoteAtPath(string[] folderPath, string noteName, string htmlContent)
    {
        string parentId = await GetOrCreateFolderPath(folderPath);

        object body = new
        {
            parentNoteId = parentId,
            title        = noteName,
            content      = htmlContent,
            type         = "text",
            mime         = "text/html"
        };

        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/create-note", payload);
        res.EnsureSuccessStatusCode();

        JsonNode result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        string noteId   = result["note"]!["noteId"]!.GetValue<string>();
        string branchId = result["branch"]!["branchId"]!.GetValue<string>();
        return (noteId, branchId);
    }

    public async Task UpdateNoteContent(string noteId, string content)
    {
        StringContent payload = new(content, Encoding.UTF8, "text/plain");
        HttpResponseMessage res = await http.PutAsync($"etapi/notes/{noteId}/content", payload);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Moves a note to a new folder path by deleting its old branch and creating a new one.
    /// Returns the new branch ID so the caller can update its cache.
    /// </summary>
    public async Task<string> MoveNoteToFolderPath(string branchId, string noteId, string[] newFolderPath)
    {
        string newParentId = await GetOrCreateFolderPath(newFolderPath);

        // Trilium ETAPI does not support changing parentNoteId via PATCH on a branch.
        // The correct approach is: delete the old branch, then create a new one.
        await http.DeleteAsync($"etapi/branches/{branchId}");

        object body = new { noteId, parentNoteId = newParentId, notePosition = 0 };
        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/branches", payload);
        res.EnsureSuccessStatusCode();

        JsonNode result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return result["branchId"]!.GetValue<string>();
    }

    /// <summary>Renames a note. Best-effort — failures are silently ignored.</summary>
    public async Task RenameNote(string noteId, string newTitle)
    {
        object body = new { title = newTitle };
        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        await http.PatchAsync($"etapi/notes/{noteId}", payload);
    }

    public async Task<List<string>> SearchNotes(string searchTerm)
    {
        string encoded = Uri.EscapeDataString(searchTerm);
        HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
        if (!res.IsSuccessStatusCode) return [];

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
        return results
            .Where(n => n is not null)
            .Select(n => n!["title"]?.GetValue<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();
    }

    /// <summary>
    /// Returns title → (noteId, folderPath) for all entity notes in the tree.
    /// Category folders (depth-1 children of root) are detected by depth, not by title.
    /// Also populates the internal folderCache for later path-based operations.
    /// </summary>
    public async Task<Dictionary<string, (string Id, string FolderPath)>> GetAllNoteIds()
    {
        List<(string Id, string Title, string FolderPath)> all = await TraverseTree();

        // Deduplicate by title: prefer notes NOT in Unknown/ over Unknown stubs that share
        // a name with a real categorised note (e.g. Unknown/People vs the real People folder).
        suppressedStubIds.Clear();
        Dictionary<string, (string Id, string FolderPath)> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string title, string folderPath) in all)
        {
            if (!result.TryGetValue(title, out (string Id, string FolderPath) existing))
            {
                result[title] = (id, folderPath);
            }
            else
            {
                // Keep whichever is not in Unknown/; if both are in Unknown, keep first.
                bool existingIsUnknown = existing.FolderPath == "Unknown";
                bool incomingIsUnknown = folderPath == "Unknown";
                if (existingIsUnknown && !incomingIsUnknown)
                {
                    Common.Logger.LogWarning("[Brain] Duplicate note title '{Title}' — preferring '{Incoming}' over Unknown stub.", title, folderPath.Length > 0 ? $"{folderPath}/{title}" : title);
                    suppressedStubIds.Add(existing.Id); // old Unknown stub — schedule for deletion
                    result[title] = (id, folderPath);
                }
                else if (!existingIsUnknown && incomingIsUnknown)
                {
                    Common.Logger.LogWarning("[Brain] Duplicate note title '{Title}' — keeping '{Existing}', ignoring Unknown stub.", title, existing.FolderPath.Length > 0 ? $"{existing.FolderPath}/{title}" : title);
                    suppressedStubIds.Add(id); // incoming Unknown stub — schedule for deletion
                }
                else
                {
                    Common.Logger.LogWarning("[Brain] Duplicate note title '{Title}' — keeping first occurrence, skipping second.", title);
                }
            }
        }
        return result;
    }

    public async Task<List<string>> GetAllNoteTitles()
        => (await GetAllNoteIds()).Keys.ToList();

    /// <summary>
    /// Deletes all Unknown/ stubs that were suppressed during the last GetAllNoteIds call
    /// because a properly-categorised note with the same title already exists.
    /// </summary>
    public async Task<int> DeleteSuppressedStubs()
    {
        if (suppressedStubIds.Count == 0) return 0;
        int deleted = 0;
        foreach (string id in suppressedStubIds.ToList())
        {
            try
            {
                if (await DeleteNote(id))
                {
                    deleted++;
                    suppressedStubIds.Remove(id);
                }
            }
            catch (Exception ex)
            {
                Common.Logger.LogWarning("[Brain] Failed to delete suppressed stub {Id}: {Message}", id, ex.Message);
            }
        }
        return deleted;
    }

    /// <summary>Deletes all known category folders (and their children) from Trilium.</summary>
    public async Task PurgeCategoryFolders()
    {
        foreach (string folderId in folderCache.Values.ToList())
        {
            try { await DeleteNote(folderId); } catch { }
        }
        folderCache.Clear();
    }

    /// <summary>Returns the first branch ID for a note, or null if not found.</summary>
    public async Task<string?> GetPrimaryBranchId(string noteId)
    {
        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}");
        if (!res.IsSuccessStatusCode) return null;
        JsonNode? node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        JsonArray? branchIds = node?["branchIds"] as JsonArray;
        return branchIds?.FirstOrDefault()?.GetValue<string>();
    }

    /// <summary>
    /// Deletes a single note. Refuses to delete if the note still has children in Trilium
    /// to prevent cascading destruction of notes that were recently moved out of a folder.
    /// Returns true if deleted, false if skipped.
    /// </summary>
    public async Task<bool> DeleteNote(string noteId)
    {
        // Safety check: fetch the note and verify it has no children before deleting.
        HttpResponseMessage checkRes = await http.GetAsync($"etapi/notes/{noteId}");
        if (checkRes.IsSuccessStatusCode)
        {
            JsonNode? node = JsonNode.Parse(await checkRes.Content.ReadAsStringAsync());
            JsonArray? children = node?["childNoteIds"] as JsonArray;
            int childCount = children?.Count(c => c?.GetValue<string>()?.StartsWith("_") == false) ?? 0;
            if (childCount > 0)
            {
                Common.Logger.LogWarning("[Brain] Refusing to delete '{NoteId}' — it still has {Count} child note(s). Skipping to prevent cascade.", noteId, childCount);
                return false;
            }
        }

        HttpResponseMessage res = await http.DeleteAsync($"etapi/notes/{noteId}");
        res.EnsureSuccessStatusCode();
        return true;
    }

    // ── Tree traversal ──────────────────────────────────────────────────────────

    private async Task<List<(string Id, string Title, string FolderPath)>> TraverseTree()
    {
        List<(string, string, string)> notes = new();
        HashSet<string> visited = new();
        await Traverse(rootNoteId, notes, visited, folderPath: "", depth: 0);
        return notes;
    }

    // depth 0 = root  |  depth 1 = category folders  |  depth 2+ = notes
    private async Task Traverse(
        string noteId,
        List<(string Id, string Title, string FolderPath)> notes,
        HashSet<string> visited,
        string folderPath,
        int depth)
    {
        if (!visited.Add(noteId)) return;

        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}");
        if (!res.IsSuccessStatusCode) return;

        JsonNode? node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (node is null) return;

        string? title = node["title"]?.GetValue<string>();

        if (depth == 1 && !string.IsNullOrWhiteSpace(title))
        {
            // Category folder — register in cache, add the folder note itself to the index
            // (so FindNoteId("People") can locate and update it), then recurse into children.
            string thisFolderPath = title;
            folderCache[thisFolderPath] = noteId;
            notes.Add((noteId, title, "")); // empty folderPath = lives at root level

            JsonArray? children = node["childNoteIds"] as JsonArray;
            foreach (JsonNode? child in children ?? new JsonArray())
            {
                string? childId = child?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(childId) && !childId.StartsWith("_"))
                    await Traverse(childId, notes, visited, thisFolderPath, depth + 1);
            }
            return;
        }

        if (depth >= 2 && !string.IsNullOrWhiteSpace(title))
            notes.Add((noteId, title, folderPath));

        JsonArray? noteChildren = node["childNoteIds"] as JsonArray;
        if (noteChildren is null) return;

        foreach (JsonNode? child in noteChildren)
        {
            string? childId = child?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(childId) || childId.StartsWith("_")) continue;
            await Traverse(childId, notes, visited, folderPath, depth + 1);
        }
    }

    // ── Folder management ───────────────────────────────────────────────────────

    /// <summary>Finds or creates the folder at the given path, returning its noteId.</summary>
    private async Task<string> GetOrCreateFolderPath(string[] pathParts)
    {
        if (pathParts.Length == 0) return rootNoteId;

        string parentId   = rootNoteId;
        string currentPath = "";

        foreach (string part in pathParts)
        {
            currentPath = currentPath.Length == 0 ? part : $"{currentPath}/{part}";

            if (folderCache.TryGetValue(currentPath, out string? cachedId))
            {
                parentId = cachedId;
                continue;
            }

            string? existingId = await FindChildByTitle(parentId, part);
            string folderId    = existingId ?? await CreateFolder(part, parentId);
            folderCache[currentPath] = folderId;
            parentId = folderId;
        }

        return parentId;
    }

    /// <summary>Finds a direct child of parentId whose title matches, without a global search.</summary>
    private async Task<string?> FindChildByTitle(string parentId, string title)
    {
        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{parentId}");
        if (!res.IsSuccessStatusCode) return null;

        JsonNode? node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        JsonArray? children = node?["childNoteIds"] as JsonArray;
        if (children is null) return null;

        foreach (JsonNode? child in children)
        {
            string? childId = child?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(childId) || childId.StartsWith("_")) continue;

            HttpResponseMessage childRes = await http.GetAsync($"etapi/notes/{childId}");
            if (!childRes.IsSuccessStatusCode) continue;

            JsonNode? childNode = JsonNode.Parse(await childRes.Content.ReadAsStringAsync());
            string? childTitle  = childNode?["title"]?.GetValue<string>();

            if (string.Equals(childTitle, title, StringComparison.OrdinalIgnoreCase))
                return childId;
        }

        return null;
    }

    private async Task<string> CreateFolder(string title, string parentId)
    {
        object body = new
        {
            parentNoteId = parentId,
            title,
            content = string.Empty,
            type    = "text",
            mime    = "text/html"
        };

        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/create-note", payload);
        res.EnsureSuccessStatusCode();

        JsonNode result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return result["note"]!["noteId"]!.GetValue<string>();
    }

    // ── Attribute API ───────────────────────────────────────────────────────────

    /// <summary>Adds a label attribute to a note. Duplicates are allowed — caller deduplicates if needed.</summary>
    public async Task CreateLabelAttribute(string noteId, string name, string value = "")
    {
        object body = new { noteId, type = "label", name, value, isInheritable = false };
        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/attributes", payload);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Returns all attributes (label or relation) on a note as (attributeId, type, name, value) tuples.</summary>
    public async Task<List<(string AttributeId, string Type, string Name, string Value)>> GetNoteAttributes(string noteId)
    {
        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}/attributes");
        if (!res.IsSuccessStatusCode) return new();

        JsonArray arr = ParseArray(await res.Content.ReadAsStringAsync());
        List<(string, string, string, string)> result = new();
        foreach (JsonNode? item in arr)
        {
            if (item is null) continue;
            string? attrId = item["attributeId"]?.GetValue<string>();
            string? type   = item["type"]?.GetValue<string>();
            string? aName  = item["name"]?.GetValue<string>();
            string? aValue = item["value"]?.GetValue<string>() ?? string.Empty;
            if (attrId is not null && type is not null && aName is not null)
                result.Add((attrId, type, aName, aValue));
        }
        return result;
    }

    /// <summary>Deletes an attribute by its ID.</summary>
    public async Task DeleteAttribute(string attributeId)
    {
        await http.DeleteAsync($"etapi/attributes/{attributeId}");
    }

    /// <summary>Returns noteIds of all notes carrying the given label, via Trilium's search syntax.</summary>
    public async Task<List<string>> SearchNoteIdsByLabel(string labelName)
    {
        string encoded = Uri.EscapeDataString($"#{labelName}");
        HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
        if (!res.IsSuccessStatusCode) return new();

        JsonArray arr = ParseArray(await res.Content.ReadAsStringAsync());
        List<string> ids = new();
        foreach (JsonNode? item in arr)
        {
            string? id = item?["noteId"]?.GetValue<string>();
            if (id is not null) ids.Add(id);
        }
        return ids;
    }

    private static JsonArray ParseArray(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            // Trilium ETAPI returns {"results": [...]} — unwrap it
            if (node is JsonObject obj && obj["results"] is JsonArray results)
                return results;
            return node as JsonArray ?? new JsonArray();
        }
        catch
        {
            return new JsonArray();
        }
    }
}
