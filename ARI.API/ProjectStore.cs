using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Common;

namespace ARI.API;

// Closed set — drives mechanics (toolset, pipeline routing). New types are a deliberate code change,
// never something minted at runtime; that's what Category is for.
public enum ProjectType { Repository, ObsidianGraph }

// ServerFs: the project's files live under Paths.ServerDir("Projects") on this server, and RootPath is
// server-managed (derived + created at project creation, never user-typed). RemoteFs: the files live on
// whichever machine the desktop app attaches from — RootPath stays null server-side; the existing
// Electron per-device local-path store (see ProjectsPage.tsx) is the only record of where.
public enum StorageBackend { ServerFs, RemoteFs }

public record Project(
    string         Id,
    string         Name,
    string         Description,
    string         Instructions,
    DateTime       CreatedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectType Type,
    // Open vocabulary, purely descriptive (search/sort/LLM context) — never mechanically significant.
    string         Category    = "",
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StorageBackend Backend = StorageBackend.ServerFs,
    string?        RootPath    = null,
    List<string>?  Attachments = null);

public class ProjectStore
{
    private readonly string _filePath;
    private readonly string _threadMapPath;
    private readonly string _attachmentsDir;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProjectStore()
    {
        string dir = Paths.ClientData;
        _filePath       = Path.Combine(dir, "Projects.json");
        _threadMapPath  = Path.Combine(dir, "thread-projects.json");
        _attachmentsDir = Path.Combine(dir, "project-attachments");
        Directory.CreateDirectory(_attachmentsDir);
    }

    // ── Projects ─────────────────────────────────────────────────────────────────

    public List<Project> GetAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new();
            try   { return JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(_filePath), JsonOpts) ?? new(); }
            catch { return new(); }
        }
    }

    public Project? Get(string id) => GetAll().FirstOrDefault(p => p.Id == id);

    public void Add(Project project)
    {
        lock (_lock) { var all = GetAll(); all.Add(project); Save(all); }
    }

    public void Update(Project project)
    {
        lock (_lock)
        {
            var all = GetAll();
            int idx = all.FindIndex(p => p.Id == project.Id);
            if (idx < 0) return;
            all[idx] = project;
            Save(all);
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            var all = GetAll();
            all.RemoveAll(p => p.Id == id);
            Save(all);
            // Clean up attachment files
            string dir = AttachmentDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private void Save(List<Project> projects)
        => File.WriteAllText(_filePath, JsonSerializer.Serialize(projects, JsonOpts));

    // ── Server-side project folder (ServerFs backend only) ──────────────────────────

    /// <summary>Derives and creates this project's folder under Paths.ServerDir("Projects") — never
    /// user-typed. Disambiguates a name collision by appending a short suffix of the project's Id.</summary>
    public static string CreateServerFolder(string projectId, string projectName)
    {
        string root = Paths.ServerDir("Projects");
        string safeName = SanitizeFolderName(projectName);
        string path = Path.Combine(root, safeName);
        if (Directory.Exists(path))
            path = Path.Combine(root, $"{safeName}-{projectId[..Math.Min(8, projectId.Length)]}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeFolderName(string name)
    {
        string safe = new(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return safe.Length == 0 ? "project" : safe;
    }

    // ── Thread → Project mapping (in-memory only — threads don't survive restarts) ──

    public Dictionary<string, string> LoadThreadMap()
    {
        // Threads are in-memory only. Any persisted map is stale — ignore it.
        if (File.Exists(_threadMapPath))
            try { File.Delete(_threadMapPath); } catch { /* best-effort */ }
        return new();
    }

    public void SaveThreadMap(Dictionary<string, string> map)
    {
        // No-op — thread→project mappings are not persisted across restarts.
    }

    // ── Project attachments ───────────────────────────────────────────────────────

    private string AttachmentDir(string projectId)
    {
        string dir = Path.Combine(_attachmentsDir, projectId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public List<string> GetAttachmentNames(string projectId)
    {
        string dir = AttachmentDir(projectId);
        return Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n != null).Cast<string>().OrderBy(n => n).ToList();
    }

    public void SaveAttachment(string projectId, string fileName, byte[] data)
    {
        string path = Path.Combine(AttachmentDir(projectId), fileName);
        File.WriteAllBytes(path, data);
    }

    public byte[]? ReadAttachment(string projectId, string fileName)
    {
        string path = Path.Combine(AttachmentDir(projectId), fileName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void DeleteAttachment(string projectId, string fileName)
    {
        string path = Path.Combine(AttachmentDir(projectId), fileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
