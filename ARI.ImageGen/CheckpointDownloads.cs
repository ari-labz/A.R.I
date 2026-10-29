using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.ImageGen;

/// <summary>A checkpoint file in a Hugging Face repo.</summary>
public sealed record HfFile(string Path, long Size);

/// <summary>Where a download has got to. Error is set when it stopped without finishing.</summary>
public sealed record CheckpointDownload(string Repo, string File, long DoneBytes, long TotalBytes, bool Finished, string? Error);

/// <summary>
/// Finds and downloads checkpoints from Hugging Face into the managed ComfyUI's models/checkpoints folder, one at a
/// time. Only <c>.safetensors</c> files from huggingface.co are fetched: that format holds plain weights, where the
/// older <c>.ckpt</c> can carry code that runs when it is loaded, and ARI never fetches from anywhere else on a
/// name it was given.
/// </summary>
public static class CheckpointDownloads
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Regex RepoRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex LinkRx = new(@"^https://huggingface\.co/([^/]+/[^/]+)/(?:resolve|blob)/[^/]+/(.+\.safetensors)(?:\?.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly object Lock = new();
    private static CheckpointDownload? current;
    private static CancellationTokenSource? cts;

    public static string CheckpointsDir => Path.Combine(Dependency.ComfyUiPath ?? Dependency.DefaultInstallPath(), "models", "checkpoints");

    public static CheckpointDownload? Current { get { lock (Lock) return current; } }

    /// <summary>Reads a pasted Hugging Face file link into its repo and path, or null when it isn't one.</summary>
    public static (string Repo, string File)? ParseLink(string link)
    {
        Match m = LinkRx.Match(link.Trim());
        return m.Success && RepoRx.IsMatch(m.Groups[1].Value) ? (m.Groups[1].Value, Uri.UnescapeDataString(m.Groups[2].Value)) : null;
    }

    public static async Task<List<string>> Search(string query, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&sort=downloads&direction=-1&limit=15");
        using HttpResponseMessage r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString() ?? "").Where(id => RepoRx.IsMatch(id)).ToList();
    }

    public static async Task<List<HfFile>> Files(string repo, CancellationToken ct)
    {
        if (!RepoRx.IsMatch(repo)) throw new ArgumentException("That doesn't look like a Hugging Face repo name.");
        using HttpResponseMessage r = await Http.GetAsync($"https://huggingface.co/api/models/{repo}/tree/main", ct);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "file" && (e.GetProperty("path").GetString() ?? "").EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            .Select(e => new HfFile(e.GetProperty("path").GetString()!,
                                    e.TryGetProperty("lfs", out JsonElement lfs) && lfs.TryGetProperty("size", out JsonElement ls) ? ls.GetInt64() : e.GetProperty("size").GetInt64()))
            .OrderBy(f => f.Path).ToList();
    }

    /// <summary>Starts a download in the background. Returns an error message, or null when it began.</summary>
    public static string? Start(string repo, string file)
    {
        if (!RepoRx.IsMatch(repo)) return "That doesn't look like a Hugging Face repo name.";
        if (!file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return "Only .safetensors files are downloaded.";
        if (file.Contains("..") || file.StartsWith('/') || file.Contains('\\')) return "That file path isn't allowed.";
        string name = Path.GetFileName(file);
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' '))) return "That file name has characters ARI won't save.";

        lock (Lock)
        {
            if (current is { Finished: false }) return $"{current.File} is still downloading.";
            current = new CheckpointDownload(repo, name, 0, 0, false, null);
            cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => Run(repo, file, name, cts!.Token));
        return null;
    }

    public static void Cancel()
    {
        lock (Lock) cts?.Cancel();
    }

    private static async Task Run(string repo, string file, string name, CancellationToken ct)
    {
        string dir  = CheckpointsDir;
        string dest = Path.Combine(dir, name);
        string part = dest + ".part";
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(dest)) throw new IOException($"{name} is already installed.");

            using HttpResponseMessage r = await Http.GetAsync($"https://huggingface.co/{repo}/resolve/main/{Uri.EscapeDataString(file).Replace("%2F", "/")}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new IOException("That repo needs a Hugging Face login, which ARI doesn't have.");
            r.EnsureSuccessStatusCode();

            long total = r.Content.Headers.ContentLength ?? 0, done = 0;
            await using (Stream src = await r.Content.ReadAsStreamAsync(ct))
            await using (FileStream dst = File.Create(part))
            {
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    lock (Lock) current = new CheckpointDownload(repo, name, done, total, false, null);
                }
            }
            File.Move(part, dest);
            lock (Lock) current = new CheckpointDownload(repo, name, done, total, true, null);
            Shared.Logger.LogInformation("[ImageGen] Downloaded {File} from {Repo}.", name, repo);
        }
        catch (Exception e)
        {
            try { File.Delete(part); } catch { }
            string why = e is OperationCanceledException ? "Cancelled." : e.Message;
            if (e is not OperationCanceledException) Shared.Logger.LogWarning(e, "[ImageGen] Download of {File} failed.", name);
            lock (Lock) current = new CheckpointDownload(repo, name, current?.DoneBytes ?? 0, current?.TotalBytes ?? 0, true, why);
        }
    }
}
