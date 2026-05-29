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

    public async Task<string?> FindNoteIdByTitle(string title, NoteCategory category)
    {
        string categoryId = await GetOrCreateCategoryFolder(category);

        string encoded = Uri.EscapeDataString(title);
        HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
        res.EnsureSuccessStatusCode();

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());

        foreach (JsonNode? item in results)
        {
            if (item is null) continue;
            string noteId = item["noteId"]!.GetValue<string>();
            string noteTitle = item["title"]!.GetValue<string>();

            if (!string.Equals(noteTitle, title, StringComparison.OrdinalIgnoreCase))
                continue;

            if (await NoteIsUnder(noteId, categoryId))
                return noteId;
        }

        return null;
    }

    // Finds a note by exact title match anywhere in Trilium, ignoring category
    public async Task<string?> FindNoteIdByTitleAnywhere(string title)
    {
        // Try two search forms — plain keyword and quoted exact match
        foreach (string query in new[] { $"\"{title}\"", title })
        {
            string encoded = Uri.EscapeDataString(query);
            HttpResponseMessage res = await http.GetAsync($"etapi/notes?search={encoded}");
            if (!res.IsSuccessStatusCode) continue;

            JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
            foreach (JsonNode? item in results)
            {
                if (item is null) continue;
                if (string.Equals(item["title"]?.GetValue<string>(), title, StringComparison.OrdinalIgnoreCase))
                    return item["noteId"]!.GetValue<string>();
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

    public async Task UpdateNoteContent(string noteId, string yamlContent)
    {
        StringContent payload = new(yamlContent, Encoding.UTF8, "text/plain");
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

    // Returns all note titles across the brain (excluding folders/root)
    public async Task<List<string>> GetAllNoteTitles()
    {
        HttpResponseMessage res = await http.GetAsync("etapi/notes?search=*");
        if (!res.IsSuccessStatusCode) return new List<string>();

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
        return results
            .Where(n => n is not null)
            .Select(n => n!["title"]?.GetValue<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();
    }

    // Creates a Trilium relation attribute linking two notes
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
        // Best-effort — don't throw if relation creation fails
    }

    public async Task<List<string>> GetAllNoteIds()
    {
        HttpResponseMessage res = await http.GetAsync("etapi/notes?search=*");
        if (!res.IsSuccessStatusCode) return new List<string>();

        JsonArray results = ParseArray(await res.Content.ReadAsStringAsync());
        return results
            .Where(n => n is not null)
            .Select(n => n!["noteId"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != rootNoteId)
            .Select(id => id!)
            .ToList();
    }

    // Deletes a note by ID
    public async Task DeleteNote(string noteId)
    {
        await http.DeleteAsync($"etapi/notes/{noteId}");
    }

    public void ClearCache() => categoryNoteIds.Clear();

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

    // Searches for a folder note by exact title match using the ETAPI search endpoint
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

    // Uses the branches endpoint to check if a note sits under a given ancestor
    private async Task<bool> NoteIsUnder(string noteId, string ancestorId)
    {
        HttpResponseMessage res = await http.GetAsync($"etapi/notes/{noteId}/branches");
        if (!res.IsSuccessStatusCode) return false;

        JsonArray branches = ParseArray(await res.Content.ReadAsStringAsync());
        foreach (JsonNode? branch in branches)
        {
            if (branch is null) continue;
            if (branch["parentNoteId"]?.GetValue<string>() == ancestorId)
                return true;
        }
        return false;
    }

    // Safely parses JSON that should be an array — returns empty array on any parse failure
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
