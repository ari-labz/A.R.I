using System.Text.Json.Serialization;

namespace ARI.API;

public class APIConfig
{
    public bool Enabled { get; init; }
    public int Port { get; init; } = 5000;
    public string LogPath { get; init; } = "";
    public GoogleAuthConfig Google { get; init; } = new();
}

public class GoogleAuthConfig
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public List<string> AllowedEmails { get; init; } = new();}