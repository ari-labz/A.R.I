using System.Net.Http.Headers;
using ARI.Common;

namespace ARI.Mesh;

public class MeshModule(MeshConfig config) : IMeshModule
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public MeshConfig Config => config;
    public bool StopLlmsDuringJob => config.StopLlmsDuringJob;
    public int  MaxFaces          => config.MaxFaces;
    public bool IsReady => !string.IsNullOrWhiteSpace(config.ServerUrl);

    public string BaseUrl => config.ServerUrl.Trim().TrimEnd('/');

    public async Task<byte[]> GenerateAsync(byte[] image, int maxFaces, long? seed = null, string format = "glb", CancellationToken ct = default)
    {
        string url = $"{BaseUrl}/generate?max_faces={maxFaces}" + (seed is { } s ? $"&seed={s}" : "") + $"&format={format}";
        using ByteArrayContent body = new(image);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using HttpResponseMessage resp = await Http.PostAsync(url, body, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"The mesh server said {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
