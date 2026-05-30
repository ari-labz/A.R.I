using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.Brain;

public class TriliumClient
{
    private readonly HttpClient http;
    private readonly string rootNoteId;

    private static readonly Dictionary<NoteCategory, string> CategoryTitles = new()
    {
        { NoteCategory.People,  "People"  },
        { NoteCategory.Places,  "Places"  },
        { NoteCategory.Events,  "Events"  },
        { NoteCategory.Unknown, "Unknown" }
    };

    private readonly Dictionary<NoteCategory, string> categoryNoteIds = new();

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

    public async Task<string> CreateNote(string title, string htmlContent, NoteCategory category)
    {
        string parentId = await GetOrCreateCategoryFolder(category);

        var body = new
        {
            parentNoteId = parentId,
            title,
            content = htmlContent,
            type = "text",
            mime = "text/html"
        };

        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/create-note", payload);
        res.EnsureSuccessStatusCode();

        JsonNode result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return result["note"]!["noteId"]!.GetValue<string>();
    }

    public async Task UpdateNoteContent(string noteId, string content)
    {
        StringContent payload = new(content, Encoding.UTF8, "text/plain");
        HttpResponseMessage res = await http.PutAsync($"etapi/notes/{noteId}/content", payload);
        res.EnsureSuccessStatusCode();
    }

    public async Task<List<string>> SearchNotes(string searchTerm)
    {
        string encoded = Uri.EscapeDataString(searchTerm);
        HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
        if (!res.IsSuccessStatusCode) return new List<string>();

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
        return results
            .Where(n => n is not null)
            .Select(n => n!["title"]?.GetValue<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();
    }

    // TODO: optimise — traverses the full note tree on every call.
    // Future: replace with a single search query once a working one is found.
    // Returns titles of entity notes only (excludes category folders).
    public async Task<List<string>> GetAllNoteTitles()
    {
        HashSet<string> folderTitles = new(CategoryTitles.Values, StringComparer.OrdinalIgnoreCase);
        List<(string Id, string Title)> all = await TraverseTree();
        return all
            .Where(n => !folderTitles.Contains(n.Title))
            .Select(n => n.Title)
            .ToList();
    }

    // Deletes all notes by cascading category folder deletion
    public async Task<int> PurgeAllNotes()
    {
        int count = 0;
        foreach (string folderTitle in CategoryTitles.Values)
        {
            string? folderId = await FindNoteIdByTitleAnywhere(folderTitle);
            if (folderId is null) continue;
            await DeleteNote(folderId);
            count++;
        }
        categoryNoteIds.Clear();
        return count;
    }

    public async Task CreateRelation(string fromNoteId, string toNoteId, string relationName = "references")
    {
        var body = new
        {
            noteId = fromNoteId,
            type = "relation",
            name = relationName,
            value = toNoteId,
            isInheritable = false
        };

        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        await http.PostAsync("etapi/attributes", payload);
    }

    public async Task DeleteNote(string noteId)
    {
        await http.DeleteAsync($"etapi/notes/{noteId}");
    }

    // ── Tree traversal ──────────────────────────────────────────────────────────

    // Walks the full note tree from root using childNoteIds — no search queries required.
    private async Task<List<(string Id, string Title)>> TraverseTree()
    {
        List<(string, string)> notes = new();
        HashSet<string> visited = new();
        await Traverse(rootNoteId, notes, visited, isRoot: true);
        return notes;
    }

    private async Task Traverse(string noteId, List<(string, string)> notes, HashSet<string> visited, bool isRoot = false)
    {
        if (!visited.Add(noteId)) return;

        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}");
        if (!res.IsSuccessStatusCode) return;

        JsonNode? node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        if (node is null) return;

        string? title = node["title"]?.GetValue<string>();
        if (!isRoot && !string.IsNullOrWhiteSpace(title))
            notes.Add((noteId, title));

        JsonArray? children = node["childNoteIds"] as JsonArray;
        if (children is null) return;

        foreach (JsonNode? child in children)
        {
            string? childId = child?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(childId) || childId.StartsWith("_")) continue;
            await Traverse(childId, notes, visited);
        }
    }

    // ── Category folder management ──────────────────────────────────────────────

    private async Task<string> GetOrCreateCategoryFolder(NoteCategory category)
    {
        if (categoryNoteIds.TryGetValue(category, out string? cached))
            return cached;

        string title = CategoryTitles[category];
        string? existingId = await SearchForFolder(title);
        string folderId = existingId ?? await CreateFolder(title, rootNoteId);
        categoryNoteIds[category] = folderId;
        return folderId;
    }

    private async Task<string?> SearchForFolder(string title)
    {
        string encoded = Uri.EscapeDataString(title);
        HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
        if (!res.IsSuccessStatusCode) return null;

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
        foreach (JsonNode? item in results)
        {
            if (item is null) continue;
            if (string.Equals(item["title"]?.GetValue<string>(), title, StringComparison.OrdinalIgnoreCase))
                return item["noteId"]!.GetValue<string>();
        }

        return null;
    }

    private async Task<string> CreateFolder(string title, string parentId)
    {
        var body = new
        {
            parentNoteId = parentId,
            title,
            content = string.Empty,
            type = "text",
            mime = "text/html"
        };

        StringContent payload = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage res = await http.PostAsync("etapi/create-note", payload);
        res.EnsureSuccessStatusCode();

        JsonNode result = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return result["note"]!["noteId"]!.GetValue<string>();
    }

    private static JsonArray ParseArray(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            return node as JsonArray ?? new JsonArray();
        }
        catch
        {
            return new JsonArray();
        }
    }
}
