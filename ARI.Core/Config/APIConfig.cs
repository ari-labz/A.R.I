using System.Text.Json.Serialization;

namespace ARI.Core;

public class APIConfig
{
    public int Port { get; init; } = 5000;
    public GoogleAuthConfig Google { get; init; } = new();
}

public class GoogleAuthConfig
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    /// <summary>Single allowed email (legacy). Use AllowedEmails for multi-user access.</summary>
    public string AllowedEmail { get; init; } = "";

    /// <summary>List of allowed emails. If non-empty, takes precedence over AllowedEmail.</summary>
    public List<string> AllowedEmails { get; init; } = new();

    /// <summary>Returns the effective allowlist — AllowedEmails if populated, otherwise the single AllowedEmail.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveAllowedEmails =>
        AllowedEmails.Count > 0 ? AllowedEmails : (AllowedEmail.Length > 0 ? new List<string> { AllowedEmail } : new List<string>());
}