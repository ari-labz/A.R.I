using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>Binds a ServerFs project directly to a caller-given absolute path instead of the
    /// auto-managed Projects/&lt;name&gt; folder — creates the directory (and any missing parents) if
    /// it doesn't exist, then initialises it exactly like CreateServerFolder. Only reached after the
    /// path has been explicitly confirmed with the user in chat — see create_project/set_project_path.</summary>
    public static string CreateServerFolderAt(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        InitAriProject(full);
        return full;
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

    /// <summary>True for a path (relative to a project or thread folder) that points into ARI's own git folder or a .git
    /// folder: nobody may read, write or delete there, because a changed git config can make git run a command.</summary>
    public static bool IsProtectedPath(string relPath)
        => relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.TrimEnd('.', ' '))   // Windows ignores trailing dots and spaces, so ".ariproject." is the same folder
            .Any(part => part.Contains('~') ||        // ...and short names like ARIPRO~1
                       part.Equals(".ariproject", StringComparison.OrdinalIgnoreCase) || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                      || part.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || part.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the file or folder, or any folder on the way down to it from <paramref name="root"/>, is really a link
    /// (a shortcut that could lead outside the project). Nothing under a link is read, written or deleted.</summary>
    public static bool PassesThroughLink(string root, string abs)
    {
        string current = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (string part in Path.GetRelativePath(current, abs).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) return true;
        }
        return false;
    }

    /// <summary>Runs a git command against the .ariproject git dir in the given folder.</summary>
    public static (int ExitCode, string Output) RunGit(string workTree, string arguments)
    {
        if (workTree.Contains('"')) return (-1, "That folder name is not allowed.");
        string ariDir = Path.Combine(workTree, ".ariproject");
        string fullArgs = $"-c core.fsmonitor= -c core.hooksPath=/dev/null -c core.symlinks=false -c core.attributesFile=/dev/null -c protocol.ext.allow=never --git-dir=\"{ariDir}\" --work-tree=\"{workTree}\" {arguments}";
        using Process proc = new Process
        {
            StartInfo = new ProcessStartInfo
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
        // Read both streams at once (so a chatty stderr can't block git) and never wait forever.
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "git took too long and was stopped.");
        }
        string output = stdout.Result + stderr.Result;
        return (proc.ExitCode, output.Trim());
    }

    private static string SanitizeFolderName(string name)
    {
        // Letters, digits, space, - _ . only: the folder name ends up in a git command line, so no quotes or other symbols.
        string safe = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '-').ToArray()).Trim('.');
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
