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
