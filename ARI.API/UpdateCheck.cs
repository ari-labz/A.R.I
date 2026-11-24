using ARI.Common;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ARI.API;

/// <summary>
/// Checks whether a newer server release exists on GitHub. A release counts if it is stable, or a pre-release of a 0.x
/// version (every 0.x release is published as a pre-release while ARI is in beta); drafts never count. The result is cached
/// and refreshed at most once every <see cref="CheckInterval"/>, so it's safe to call on every request. Any failure
/// (offline, rate-limited, private repo without a token) leaves the previous state untouched — we never nag on a failed check.
/// </summary>
public static class UpdateCheck
{
    private const string Owner = "ari-labz";
    private const string Repo  = "A.R.I";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public  static string  ServerVersion { get; } = BuildInfo.Version;
    public  static string? LatestVersion { get; private set; }
    public  static bool    Outdated      { get; private set; }
    private static DateTime _lastChecked = DateTime.MinValue;
    private static IReadOnlyDictionary<string, string> _latestAssets = new Dictionary<string, string>();

    /// <summary>The download link of one of the latest release's files, by name (e.g. ARI_Server_v0.11.4_mac.zip).</summary>
    public static string? AssetUrl(string name) => _latestAssets.GetValueOrDefault(name);

    /// <param name="force">Ask GitHub now instead of using a result up to <see cref="CheckInterval"/> old.</param>
    public static async Task EnsureCheckedAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastChecked < CheckInterval)
            return;

        await Gate.WaitAsync();
        try
        {
            if (!force && DateTime.UtcNow - _lastChecked < CheckInterval)
                return;

            (string Version, Dictionary<string, string> Assets)? latest = await FetchLatestAsync();
            _lastChecked = DateTime.UtcNow;
            if (latest is null)
                return;   // check failed — keep the last known state

            LatestVersion = latest.Value.Version;
            _latestAssets = latest.Value.Assets;
            Outdated      = CompareVersions(LatestVersion, ServerVersion) > 0;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The newest release that counts (see the class summary) and its files, or null if the check failed.</summary>
    private static async Task<(string Version, Dictionary<string, string> Assets)?> FetchLatestAsync()
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30");
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

            (string Version, Dictionary<string, string> Assets)? best = null;
            foreach (JsonElement r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()) continue;
                string? version = r.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString()?.TrimStart('v', 'V') : null;
                if (string.IsNullOrEmpty(version)) continue;
                bool pre = r.TryGetProperty("prerelease", out JsonElement p) && p.GetBoolean();
                if (pre && !version.StartsWith("0.", StringComparison.Ordinal)) continue;
                if (best is not null && CompareVersions(version, best.Value.Version) <= 0) continue;

                Dictionary<string, string> assets = new(StringComparer.OrdinalIgnoreCase);
                if (r.TryGetProperty("assets", out JsonElement list))
                    foreach (JsonElement a in list.EnumerateArray())
                        if (a.GetProperty("name").GetString() is { } n && a.GetProperty("browser_download_url").GetString() is { } u)
                            assets[n] = u;
                best = (version, assets);
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    // Returns >0 if a is newer than b.
    public static int CompareVersions(string a, string b)
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
