namespace ARI.API.Auth;

public class User
{
    public int    Id                 { get; set; }
    public string Username           { get; set; } = "";
    public string PasswordHash       { get; set; } = "";
    public string Role               { get; set; } = Roles.Guest;
    public string DisplayName        { get; set; } = "";
    public bool   MustChangePassword { get; set; }
    public long   CreatedAt          { get; set; }
    public long   LastActiveAt       { get; set; }
}

/// <summary>A personal access token: a long-lived password-like key a person makes for a script or app. Only a hash is stored.</summary>
public class PersonalToken
{
    public string Id         { get; set; } = "";
    public int    UserId     { get; set; }
    public string Name       { get; set; } = "";
    /// <summary>The first characters of the token, so a person can tell their tokens apart. Not enough to use it.</summary>
    public string Prefix     { get; set; } = "";
    public long   CreatedAt  { get; set; }
    public long   LastUsedAt { get; set; }
}

public class UserSession
{
    public string SessionId  { get; set; } = "";
    public int    UserId     { get; set; }
    public long   ExpiresAt  { get; set; }
    public string DeviceHint { get; set; } = "";
    public bool   IsDesktop  { get; set; }
    public long   LastUsedAt { get; set; }
}

public class BlockedIp
{
    public string Ip             { get; set; } = "";
    public long   BlockedAt      { get; set; }
    public int    FailedAttempts { get; set; }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Guest = "Guest";
}
