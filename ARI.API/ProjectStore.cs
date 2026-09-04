using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ARI.Common;

namespace ARI.API;

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
    // Open vocabulary, purely descriptive (search/sort/LLM context) — never mechanically significant.
    string         Category    = "",
    // Default only matters for a row missing this field (every project created before Backend existed) —
    // Create() always computes an explicit value, never relying on this. RemoteFs is the correct default
    // for those legacy rows: ServerFs didn't exist yet, so every one of them has, by construction, been
    // relying on the desktop app's own per-device local-path store (see ProjectsPage.tsx) this whole time.
    [property: JsonConverter(typeof(JsonStringEnumConverter))] StorageBackend Backend = StorageBackend.RemoteFs,
    string?        RootPath    = null,
    // 0 = admin-owned (legacy rows that predate multi-user support).
    int            OwnerId     = 0);

public class ProjectStore
{
    private readonly string _filePath;
    private readonly string _threadMapPath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProjectStore()
    {
        string dir = Paths.ClientData;
        _filePath      = Path.Combine(dir, "Projects.json");
        _threadMapPath = Path.Combine(dir, "thread-projects.json");
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

    public List<Project> GetByOwner(int ownerId) => GetAll().Where(p => p.OwnerId == ownerId).ToList();

    public void Add(Project project)
    {
        lock (_lock) { List<Project> all = GetAll(); all.Add(project); Save(all); }
    }

    public void Update(Project project)
    {
        lock (_lock)
        {
            List<Project> all = GetAll();
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
            List<Project> all = GetAll();
            all.RemoveAll(p => p.Id == id);
            Save(all);
        }
    }

    private void Save(List<Project> projects)
        => File.WriteAllText(_filePath, JsonSerializer.Serialize(projects, JsonOpts));

    // ── Server-side project folder (ServerFs backend only) ──────────────────────────

    /// <summary>Derives and creates this project's folder under Paths.ServerDir("Projects") — never
    /// user-typed. Disambiguates a name collision by appending a short suffix of the project's Id.
    /// Also initialises an .ariproject hidden git repo and .ariignore for ARI Project Sync.</summary>
    public static string CreateServerFolder(string projectId, string projectName)
    {
        string root = Paths.ServerDir("Projects");
        string safeName = SanitizeFolderName(projectName);
        string path = Path.Combine(root, safeName);
        if (Directory.Exists(path))
            path = Path.Combine(root, $"{safeName}-{projectId[..Math.Min(8, projectId.Length)]}");
        Directory.CreateDirectory(path);
        InitAriProject(path);
        return path;
    }

    /// <summary>Initialises a .ariproject hidden git repo (ARI Project Sync) in the given folder.
    /// Uses --git-dir=.ariproject so standard git tools never detect this as a repository.
    /// Safe to call on an already-initialised folder.</summary>
    public static void InitAriProject(string folderPath)
    {
        string ariDir = Path.Combine(folderPath, ".ariproject");
        if (Directory.Exists(ariDir)) return;

        WriteAriIgnore(folderPath);
        RunGit(folderPath, "init");
        // Copy .ariignore into info/exclude so `git add --all` honours it natively.
        // `git add` has no --exclude-from flag; info/exclude is the correct mechanism.
        SyncExcludeFile(folderPath);
        RunGit(folderPath, "add --all");
        // Commit whatever exists; fall back to an empty commit for a brand-new folder.
        (int code, _) = RunGit(folderPath, "commit -m \"Init\"");
        if (code != 0) RunGit(folderPath, "commit --allow-empty -m \"Init\"");
    }

    private static void SyncExcludeFile(string folderPath)
    {
        string ariIgnore = Path.Combine(folderPath, ".ariignore");
        if (!File.Exists(ariIgnore)) return;
        string infoDir = Path.Combine(folderPath, ".ariproject", "info");
        Directory.CreateDirectory(infoDir);
        File.Copy(ariIgnore, Path.Combine(infoDir, "exclude"), overwrite: true);
    }

    private static void WriteAriIgnore(string folderPath)
    {
        string path = Path.Combine(folderPath, ".ariignore");
        if (File.Exists(path)) return;
        File.WriteAllText(path, """
            # Inner git repos — tracked by their own remotes, not ARI Project Sync
            **/.git
            # Build outputs
            node_modules/
            bin/
            obj/
            dist/
            devbuild/
            *.user
            .ariproject/
            """);
    }

    /// <summary>Runs a git command against the .ariproject git dir in the given folder.</summary>
    public static (int ExitCode, string Output) RunGit(string workTree, string arguments)
    {
        string ariDir = Path.Combine(workTree, ".ariproject");
        string fullArgs = $"--git-dir=\"{ariDir}\" --work-tree=\"{workTree}\" {arguments}";
        using System.Diagnostics.Process proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "git",
                Arguments              = fullArgs,
                WorkingDirectory       = workTree,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            }
        };
        proc.Start();
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output.Trim());
    }

    private static string SanitizeFolderName(string name)
    {
        string safe = new(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return safe.Length == 0 ? "project" : safe;
    }

    // ── Migration ─────────────────────────────────────────────────────────────────

    /// <summary>Ensures every project has a server folder. Converts legacy RemoteFs projects to
    /// ServerFs by creating their folder under Paths.ServerDir("Projects") if missing.</summary>
    public void MigrateToServerFs()
    {
        lock (_lock)
        {
            List<Project> all = GetAll();
            bool changed = false;
            for (int i = 0; i < all.Count; i++)
            {
                Project p = all[i];
                if (p.Backend == StorageBackend.ServerFs && p.RootPath is not null) continue;
                string root = p.RootPath ?? CreateServerFolder(p.Id, p.Name);
                InitAriProject(root);
                all[i]  = p with { Backend = StorageBackend.ServerFs, RootPath = root };
                changed = true;
            }
            if (changed) Save(all);
        }
    }

    // ── Thread → Project mapping (in-memory only — threads don't survive restarts) ──
    // Owned here (not by a controller) so both ThreadsController and ProjectServiceAdapter — the
    // REST path and the tool-call path — read/write the exact same shared state.

    private ConcurrentDictionary<string, string>? _threadProjects;
    public ConcurrentDictionary<string, string> ThreadProjects
        => _threadProjects ??= new ConcurrentDictionary<string, string>(LoadThreadMap());

    public void BindThread(string threadKey, string projectId) => ThreadProjects[threadKey] = projectId;

    public Dictionary<string, string> LoadThreadMap()
    {
        // Threads are in-memory only. Any persisted map is stale — ignore it.
        if (File.Exists(_threadMapPath))
            try { File.Delete(_threadMapPath); } catch { /* best-effort */ }
        return new();
    }

}
