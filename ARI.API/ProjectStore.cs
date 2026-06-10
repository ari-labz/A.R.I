using System.Text.Json;

namespace ARI.API;

public record Project(
    string       Id,
    string       Name,
    string       Description,
    string       Instructions,
    DateTime     CreatedAt,
    List<string>? Attachments      = null,
    bool         ForceCodePipeline = true);

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
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari");
        Directory.CreateDirectory(dir);
        _filePath       = Path.Combine(dir, "projects.json");
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
