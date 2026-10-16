using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.LLM;

namespace ARI.API.Setup;

public record ModelFile(string Path, string Name, long SizeBytes, string Quant, string DownloadUrl);
public record RepoListing(string Repo, List<ModelFile> Files, GgufReader.KvArchParams? Arch, string? Error);

/// <summary>Turns a pasted Hugging Face link into a list of single-file GGUFs with sizes, and sizes the KV cache
/// from the first few MB of one file's header (the same GgufReader the control panel's RAM estimate uses).</summary>
public static class ModelInspector
{
    private static readonly HttpClient http = MakeClient();

    private static HttpClient MakeClient()
    {
        HttpClient c = new() { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ARI", "1.0"));
        return c;
    }

    private static readonly Regex Shard = new(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);
    private static readonly Regex QuantRx = new(@"(IQ\d_[A-Z]+(?:_[A-Z]+)?|Q\d_K_[SML]|Q\d_[01]|Q\d_K|BF16|F16|F32|MXFP4)", RegexOptions.IgnoreCase);

    /// <summary>Accepts owner/repo, a repo URL, or a direct .../resolve|blob/branch/file.gguf link.</summary>
    public static bool TryParse(string input, out string repo, out string? file)
    {
        repo = ""; file = null;
        string s = input.Trim();
        if (s.Length == 0) return false;
        s = Regex.Replace(s, @"^https?://(www\.)?huggingface\.co/", "", RegexOptions.IgnoreCase);
        s = s.Split('?', '#')[0].Trim('/');
        string[] parts = s.Split('/');
        if (parts.Length < 2 || parts[0].Contains('.') && parts[0].Contains(' ')) return false;
        repo = parts[0] + "/" + parts[1];
        if (parts.Length >= 5 && (parts[2] is "resolve" or "blob") && parts[^1].EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            file = string.Join('/', parts.Skip(4));
        return Regex.IsMatch(repo, @"^[\w.\-]+/[\w.\-]+$");
    }

    public static async Task<RepoListing> ListAsync(string repo, string? onlyFile, CancellationToken ct)
    {
        List<ModelFile> files = new();
        try
        {
            using HttpResponseMessage r = await http.GetAsync($"https://huggingface.co/api/models/{repo}/tree/main?recursive=true", ct);
            if (r.StatusCode is System.Net.HttpStatusCode.NotFound)
                return new(repo, files, null, "Repository not found on Hugging Face.");
            if (r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new(repo, files, null, "This repository is private or gated — ARI can only download public models for now.");
            r.EnsureSuccessStatusCode();

            using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                string path = e.GetProperty("path").GetString() ?? "";
                if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                if (Shard.IsMatch(path)) continue;                      // split models: not supported by the downloader yet
                if (onlyFile is not null && !path.Equals(onlyFile, StringComparison.OrdinalIgnoreCase)) continue;
                long size = e.TryGetProperty("lfs", out JsonElement lfs) && lfs.TryGetProperty("size", out JsonElement ls) ? ls.GetInt64()
                          : e.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                Match q = QuantRx.Match(name);
                files.Add(new(path, name, size, q.Success ? q.Value.ToUpperInvariant() : "", $"https://huggingface.co/{repo}/resolve/main/{path}"));
            }
        }
        catch (Exception ex)
        {
            return new(repo, files, null, $"Could not reach Hugging Face: {ex.Message}");
        }

        if (files.Count == 0)
            return new(repo, files, null, "No single-file GGUF models found in that repository.");

        files = files.OrderBy(f => f.SizeBytes).ToList();
        GgufReader.KvArchParams? arch = await ReadArchAsync(files[files.Count / 2].DownloadUrl, ct);
        return new(repo, files, arch, null);
    }

    private static async Task<GgufReader.KvArchParams?> ReadArchAsync(string url, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 16L * 1024 * 1024 - 1);
            using HttpResponseMessage r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!r.IsSuccessStatusCode) return null;
            using MemoryStream ms = new();
            using Stream s = await r.Content.ReadAsStreamAsync(ct);
            await s.CopyToAsync(ms, ct);
            ms.Position = 0;
            return GgufReader.TryRead(ms);
        }
        catch { return null; }
    }

    public const long Overhead = 1L << 30;   // compute buffers, CUDA context

    /// <summary>KV-cache bytes per token at 8-bit K and V — the setup wizard's default.</summary>
    public static double KvBytesPerToken(GgufReader.KvArchParams? arch, long weightsBytes)
    {
        const double bytesPerElem = 1.0;
        if (arch is not null) return (double)arch.NLayers * arch.NKvHeads * arch.HeadDim * 2 * bytesPerElem;
        double billions = weightsBytes * 8.0 / (4.5 * 1e9);     // same rough fallback as the control panel
        return (billions / 7.0) * 500 * 1024 * 1024 / 1024 * 0.5;
    }

    public static readonly int[] ContextSteps = [4096, 8192, 16384, 32768, 65536, 131072];

    /// <summary>Largest standard context size that still fits the usable memory, 0 if even weights don't.</summary>
    public static int MaxContext(long usable, long weights, double kvPerToken)
    {
        long free = usable - weights - Overhead;
        if (free <= 0) return 0;
        int best = 0;
        foreach (int c in ContextSteps) if (kvPerToken * c <= free) best = c;
        return best;
    }
}
