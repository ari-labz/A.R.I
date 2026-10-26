using System.Text.RegularExpressions;

namespace ARI.API.Auth;

/// <summary>The one rule for usernames and passwords, used everywhere an account is made or changed.</summary>
public static class AccountRules
{
    public const int MinPasswordLength = 8;
    public const int MinUsernameLength = 2;
    public const int MaxUsernameLength = 40;

    private static readonly Regex UsernamePattern = new(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled);

    /// <summary>Null if the password is acceptable, otherwise the message to show.</summary>
    public static string? CheckPassword(string? password)
        => (password ?? "").Length >= MinPasswordLength ? null : $"Password must be at least {MinPasswordLength} characters.";

    /// <summary>Null if the (already trimmed) username is acceptable, otherwise the message to show.</summary>
    public static string? CheckUsername(string? username)
    {
        string name = username ?? "";
        if (name.Length < MinUsernameLength || name.Length > MaxUsernameLength)
            return $"Username must be {MinUsernameLength} to {MaxUsernameLength} characters.";
        if (!UsernamePattern.IsMatch(name))
            return "Username may only contain letters, numbers, underscores, hyphens and dots.";
        return null;
    }
}
