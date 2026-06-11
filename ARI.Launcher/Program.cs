using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

// ── Config ────────────────────────────────────────────────────────────────────

const string Owner = "Xywren";
const string Repo  = "A.R.I";

// ── Paths ─────────────────────────────────────────────────────────────────────

string baseDir = GetBaseDir();
Directory.CreateDirectory(baseDir);

string tokenFile = Path.Combine(baseDir, "github_token.txt");

// ── Entry point ───────────────────────────────────────────────────────────────

Console.WriteLine($"[ARI Launcher] Base directory: {baseDir}");

string? token = GetToken(tokenFile);
if (token is null)
{
    token = PromptForToken(tokenFile);
    if (token is null) { Console.Error.WriteLine("[ARI Launcher] No GitHub token provided. Cannot check for releases."); return 1; }
}

Release? latest = await FetchLatestRelease(token);
if (latest is null) { Console.Error.WriteLine("[ARI Launcher] Could not fetch latest release from GitHub."); return 1; }

string versionDir = Path.Combine(baseDir, latest.TagName.TrimStart('v'));

if (!Directory.Exists(versionDir) || !Directory.EnumerateFileSystemEntries(versionDir).Any())
{
    Console.WriteLine($"[ARI Launcher] Installing {latest.TagName}...");
    bool ok = await DownloadAndInstall(latest, versionDir, token);
    if (!ok) { Console.Error.WriteLine("[ARI Launcher] Installation failed."); return 1; }
    Console.WriteLine($"[ARI Launcher] Installed to {versionDir}");
}
else
{
    Console.WriteLine($"[ARI Launcher] {latest.TagName} already installed.");
}

// Clean up old versions (keep latest 2)
CleanOldVersions(baseDir, latest.TagName.TrimStart('v'));

string? executable = FindExecutable(versionDir);
if (executable is null) { Console.Error.WriteLine($"[ARI Launcher] Could not find ARI executable in {versionDir}"); return 1; }

Console.WriteLine($"[ARI Launcher] Launching {executable}");
Launch(executable);
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────────

static string GetBaseDir()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARI");
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ARI");
    // Linux
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ARI");
}

static string? GetToken(string tokenFile)
{
    string? env = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
    if (File.Exists(tokenFile))
    {
        string t = File.ReadAllText(tokenFile).Trim();
        if (!string.IsNullOrWhiteSpace(t)) return t;
    }
    return null;
}

static string? PromptForToken(string tokenFile)
{
    Console.WriteLine();
    Console.WriteLine("ARI is hosted on a private GitHub repository.");
    Console.WriteLine("A GitHub personal access token is required to download releases.");
    Console.WriteLine("Create one at: https://github.com/settings/tokens (needs 'repo' scope)");
    Console.Write("Enter token: ");
    string? token = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(token)) return null;
    File.WriteAllText(tokenFile, token);
    Console.WriteLine("Token saved.");
    return token;
}

static async Task<Release?> FetchLatestRelease(string token)
{
    using HttpClient http = MakeClient(token);
    try
    {
        string json = await http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string tagName = root.GetProperty("tag_name").GetString() ?? "";
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => new ReleaseAsset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();

        return new Release(tagName, assets);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ARI Launcher] GitHub API error: {ex.Message}");
        return null;
    }
}

static async Task<bool> DownloadAndInstall(Release release, string versionDir, string token)
{
    string assetName = GetAssetName(release.TagName);
    ReleaseAsset? asset = release.Assets.FirstOrDefault(a => a.Name == assetName);
    if (asset is null)
    {
        Console.Error.WriteLine($"[ARI Launcher] Asset '{assetName}' not found in release {release.TagName}. Available: {string.Join(", ", release.Assets.Select(a => a.Name))}");
        return false;
    }

    string zipPath = Path.Combine(Path.GetTempPath(), asset.Name);
    Console.WriteLine($"[ARI Launcher] Downloading {asset.Name}...");

    using HttpClient http = MakeClient(token);
    // GitHub asset downloads require Accept header for redirects
    http.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

    using (HttpResponseMessage response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead))
    {
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        using Stream src  = await response.Content.ReadAsStreamAsync();
        using Stream dest = File.Create(zipPath);

        byte[] buf       = new byte[81920];
        long   received  = 0;
        int    read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read));
            received += read;
            if (total > 0)
            {
                int pct = (int)(received * 100 / total.Value);
                Console.Write($"\r[ARI Launcher] {pct}% ({received / 1_048_576} MB / {total.Value / 1_048_576} MB)  ");
            }
        }
        Console.WriteLine();
    }

    Console.WriteLine($"[ARI Launcher] Extracting to {versionDir}...");
    Directory.CreateDirectory(versionDir);
    ZipFile.ExtractToDirectory(zipPath, versionDir, overwriteFiles: true);
    File.Delete(zipPath);

    // On macOS/Linux make the executable bit set
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        foreach (string f in Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(f);
            if (name == "ARI" || name == "ari" || !Path.HasExtension(f))
            {
                try { Process.Start("chmod", $"+x \"{f}\"")?.WaitForExit(); } catch { }
            }
        }
    }

    return true;
}

static string GetAssetName(string tagName)
{
    string version = tagName.TrimStart('v');
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"ARI-{version}-win.zip";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return $"ARI-{version}-mac.zip";
    return $"ARI-{version}-linux.zip";
}

static string? FindExecutable(string versionDir)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return Directory.GetFiles(versionDir, "ARI.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.GetFiles(versionDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
    }
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Electron mac zip produces ARI.app bundle
        string[] apps = Directory.GetDirectories(versionDir, "*.app", SearchOption.AllDirectories);
        if (apps.Length > 0)
            return Path.Combine(apps[0], "Contents", "MacOS", "ARI");
        return Directory.GetFiles(versionDir, "ARI", SearchOption.AllDirectories).FirstOrDefault();
    }
    // Linux
    return Directory.GetFiles(versionDir, "ARI", SearchOption.AllDirectories).FirstOrDefault()
        ?? Directory.GetFiles(versionDir, "ari", SearchOption.AllDirectories).FirstOrDefault();
}

static void Launch(string executable)
{
    var psi = new ProcessStartInfo(executable)
    {
        UseShellExecute = true,
    };
    Process.Start(psi);
}

static void CleanOldVersions(string baseDir, string keepVersion)
{
    var versionDirs = Directory.GetDirectories(baseDir)
        .Select(d => (path: d, name: Path.GetFileName(d)))
        .Where(d => Version.TryParse(d.name, out _))
        .OrderByDescending(d => Version.Parse(d.name))
        .ToList();

    // Always keep the current version plus one previous
    foreach (var (path, name) in versionDirs.Skip(2))
    {
        if (name == keepVersion) continue;
        try
        {
            Console.WriteLine($"[ARI Launcher] Removing old version {name}");
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ARI Launcher] Could not remove {name}: {ex.Message}");
        }
    }
}

static HttpClient MakeClient(string token)
{
    var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",     "ARILauncher/1.0");
    http.DefaultRequestHeaders.Add("Authorization",  $"token {token}");
    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    return http;
}

// ── Types ─────────────────────────────────────────────────────────────────────

record Release(string TagName, List<ReleaseAsset> Assets);
record ReleaseAsset(string Name, string Url);
