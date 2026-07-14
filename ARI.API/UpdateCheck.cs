using System.Net.Http.Headers;
using System.Text.Json;

namespace ARI.API;

/// <summary>
/// Checks whether a newer stable server release exists on GitHub. The result is cached and
/// refreshed at most once every <see cref="CheckInterval"/>, so it's safe to call on every
/// request. GitHub's releases/latest endpoint already excludes pre-releases and drafts, so a
/// pre-release never triggers the outdated banner. Any failure (offline, rate-limited, private
/// repo without a token) leaves the previous state untouched — we never nag on a failed check.
/// </summary>
public static class UpdateCheck
{
    private const string Owner = "ari-labz";
    private const string Repo  = "A.R.I";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public  static string  ServerVersion { get; } = ReadServerVersion();
    public  static string? LatestVersion { get; private set; }
    public  static bool    Outdated      { get; private set; }
    private static DateTime _lastChecked = DateTime.MinValue;

    public static async Task EnsureCheckedAsync()
    {
        if (DateTime.UtcNow - _lastChecked < CheckInterval)
            return;

        await Gate.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _lastChecked < CheckInterval)
                return;

            string? latest = await FetchLatestAsync();
            _lastChecked = DateTime.UtcNow;
            if (latest is null)
                return;   // check failed — keep the last known state

            LatestVersion = latest;
            Outdated      = CompareVersions(latest, ServerVersion) > 0;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> FetchLatestAsync()
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("ARI-Server/1.0");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            // Optional — only needed while the repo is private. Public repos work token-free.
            string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

            using HttpResponseMessage res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                return null;

            await using Stream stream = await res.Content.ReadAsStreamAsync();
            using JsonDocument doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("tag_name", out JsonElement tag)
                ? tag.GetString()?.TrimStart('v', 'V')
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadServerVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("version", out JsonElement v))
                    return v.GetString() ?? "0.0.0";
            }
        }
        catch { /* fall through */ }
        return "0.0.0";
    }

    // Returns >0 if a is newer than b.
    private static int CompareVersions(string a, string b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int da = i < pa.Length ? pa[i] : 0;
            int db = i < pb.Length ? pb[i] : 0;
            if (da != db) return da - db;
        }
        return 0;

        static int[] Parse(string t) =>
            t.TrimStart('v', 'V')
             .Split('.', '-', '+')
             .Select(s => int.TryParse(s, out int n) ? n : 0)
             .ToArray();
    }
}
