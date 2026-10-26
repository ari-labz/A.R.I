namespace ARI.API;

public class APIConfig
{
    public bool   Enabled { get; init; }
    public int    Port    { get; init; } = 5000;
    public string LogPath { get; init; } = "";

    /// <summary>Set to true only when ARI sits behind a reverse proxy or tunnel you control (Cloudflare, Tailscale
    /// serve, nginx...). Then the client's address and whether it used HTTPS are taken from the proxy's
    /// X-Forwarded headers. Left false, those headers are ignored, because anyone could forge them.</summary>
    public bool   TrustForwardedHeaders { get; init; }
}
