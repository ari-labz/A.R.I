using ARI.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ARI.API.Auth;

/// <summary>
/// SQLite-backed store for users, sessions, and the IP blocklist.
/// Lives at Paths.UsersDb — never exposed to the network; ARI owns all access.
/// On startup: if no users exist, creates an admin account with a random 8-char password
/// printed to the log.
/// </summary>
public class UserStore
{
    private readonly string            connStr;
    private readonly ILogger<UserStore> log;

    public UserStore(ILogger<UserStore> log)
    {
        this.log = log;
        connStr  = $"Data Source={Paths.UsersDb}";
        InitSchema();
        EnsureAdminExists();
        LogPendingAdminPassword();
    }

    // ── Schema ──────────────────────────────────────────────────────────────────

    private void InitSchema()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (
                Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                Username           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                PasswordHash       TEXT    NOT NULL,
                Role               TEXT    NOT NULL DEFAULT 'Guest',
                DisplayName        TEXT    NOT NULL DEFAULT '',
                MustChangePassword INTEGER NOT NULL DEFAULT 1,
                CreatedAt          INTEGER NOT NULL,
                LastActiveAt       INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId  TEXT    PRIMARY KEY,
                UserId     INTEGER NOT NULL,
                ExpiresAt  INTEGER NOT NULL,
                DeviceHint TEXT    NOT NULL DEFAULT '',
                IsDesktop  INTEGER NOT NULL DEFAULT 0,
                LastUsedAt INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS IpBlocklist (
                Ip             TEXT PRIMARY KEY,
                BlockedAt      INTEGER NOT NULL,
                FailedAttempts INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void EnsureAdminExists()
    {
        if (CountUsers() > 0) return;

        string password = AuthService.GenerateRandomPassword(8);
        string hash     = BCrypt.Net.BCrypt.HashPassword(password);
        long   now      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users (Username, PasswordHash, Role, DisplayName, MustChangePassword, CreatedAt)
            VALUES ('admin', $hash, 'Admin', 'Admin', 1, $now)
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now",  now);
        cmd.ExecuteNonQuery();

        log.LogWarning("No users found. Created admin account. Password: {Password}", password);
    }

    /// <summary>If the admin account still requires a password change, resets it to a fresh random
    /// password and logs it at Warning level. Runs on every boot so a missed log line is never fatal.</summary>
    private void LogPendingAdminPassword()
    {
        User? admin = GetByUsername("admin");
        if (admin is null || !admin.MustChangePassword) return;

        string password = AuthService.GenerateRandomPassword(8);
        string hash     = BCrypt.Net.BCrypt.HashPassword(password);
        SetPassword(admin.Id, hash, mustChange: true);

        log.LogWarning("Admin password has not been changed. Temporary password: {Password}", password);
    }

    private int CountUsers()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Users ────────────────────────────────────────────────────────────────────

    public User? GetByUsername(string username)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users WHERE Username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", username);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    public User? GetById(int id)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    public List<User> GetAll()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Users ORDER BY CreatedAt";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<User> list = new List<User>();
        while (r.Read()) list.Add(ReadUser(r));
        return list;
    }

    public User CreateUser(string username, string passwordHash, string role, string displayName)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Users (Username, PasswordHash, Role, DisplayName, MustChangePassword, CreatedAt)
            VALUES ($u, $ph, $role, $dn, 1, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$u",    username);
        cmd.Parameters.AddWithValue("$ph",   passwordHash);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$dn",   displayName);
        cmd.Parameters.AddWithValue("$now",  now);
        int id = Convert.ToInt32(cmd.ExecuteScalar());
        return GetById(id)!;
    }

    public void SetPassword(int userId, string passwordHash, bool mustChange)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET PasswordHash = $ph, MustChangePassword = $mc WHERE Id = $id";
        cmd.Parameters.AddWithValue("$ph", passwordHash);
        cmd.Parameters.AddWithValue("$mc", mustChange ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    public void SetDisplayName(int userId, string name)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET DisplayName = $dn WHERE Id = $id";
        cmd.Parameters.AddWithValue("$dn", name);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns false if the new username is already taken.</summary>
    public bool SetUsername(int userId, string newUsername)
    {
        if (GetByUsername(newUsername) is { } existing && existing.Id != userId) return false;
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET Username = $u WHERE Id = $id";
        cmd.Parameters.AddWithValue("$u",   newUsername);
        cmd.Parameters.AddWithValue("$id",  userId);
        cmd.ExecuteNonQuery();
        return true;
    }

    public void TouchLastActive(int userId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET LastActiveAt = $now WHERE Id = $id";
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$id",  userId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteUser(int userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Users WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    // ── Sessions ─────────────────────────────────────────────────────────────────

    public void CreateSession(UserSession session)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Sessions (SessionId, UserId, ExpiresAt, DeviceHint, IsDesktop, LastUsedAt)
            VALUES ($sid, $uid, $exp, $dh, $id, $lu)
            """;
        cmd.Parameters.AddWithValue("$sid", session.SessionId);
        cmd.Parameters.AddWithValue("$uid", session.UserId);
        cmd.Parameters.AddWithValue("$exp", session.ExpiresAt);
        cmd.Parameters.AddWithValue("$dh",  session.DeviceHint);
        cmd.Parameters.AddWithValue("$id",  session.IsDesktop ? 1 : 0);
        cmd.Parameters.AddWithValue("$lu",  session.LastUsedAt);
        cmd.ExecuteNonQuery();
    }

    public UserSession? GetSession(string sessionId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sessions WHERE SessionId = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    public List<UserSession> GetSessionsForUser(int userId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sessions WHERE UserId = $uid AND ExpiresAt > $now ORDER BY LastUsedAt DESC";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$now", now);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<UserSession> list = new List<UserSession>();
        while (r.Read()) list.Add(ReadSession(r));
        return list;
    }

    public List<UserSession> GetAllActiveSessions()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Sessions WHERE ExpiresAt > $now ORDER BY LastUsedAt DESC";
        cmd.Parameters.AddWithValue("$now", now);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<UserSession> list = new List<UserSession>();
        while (r.Read()) list.Add(ReadSession(r));
        return list;
    }

    public void RevokeSession(string sessionId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE SessionId = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void RevokeAllSessionsForUser(int userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE UserId = $uid";
        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.ExecuteNonQuery();
    }

    public void TouchSession(string sessionId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Sessions SET LastUsedAt = $now WHERE SessionId = $sid";
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    // ── IP Blocklist ──────────────────────────────────────────────────────────────

    public bool IsBlocked(string ip)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM IpBlocklist WHERE Ip = $ip AND FailedAttempts >= 3";
        cmd.Parameters.AddWithValue("$ip", ip);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Increments failure count for an IP. Returns the new count.
    /// At 3 failures the IP is considered blocked; callers should trigger a notification.
    /// </summary>
    public int RecordFailedAttempt(string ip)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO IpBlocklist (Ip, BlockedAt, FailedAttempts)
            VALUES ($ip, $now, 1)
            ON CONFLICT(Ip) DO UPDATE SET
                FailedAttempts = FailedAttempts + 1,
                BlockedAt      = $now
            """;
        cmd.Parameters.AddWithValue("$ip",  ip);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using SqliteCommand readCmd = conn.CreateCommand();
        readCmd.CommandText = "SELECT FailedAttempts FROM IpBlocklist WHERE Ip = $ip";
        readCmd.Parameters.AddWithValue("$ip", ip);
        return Convert.ToInt32(readCmd.ExecuteScalar());
    }

    public void ClearFailedAttempts(string ip)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM IpBlocklist WHERE Ip = $ip";
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.ExecuteNonQuery();
    }

    public List<BlockedIp> GetBlockedIps()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM IpBlocklist WHERE FailedAttempts >= 3 ORDER BY BlockedAt DESC";
        using SqliteDataReader r = cmd.ExecuteReader();
        List<BlockedIp> list = new List<BlockedIp>();
        while (r.Read())
            list.Add(new BlockedIp
            {
                Ip             = r.GetString(0),
                BlockedAt      = r.GetInt64(1),
                FailedAttempts = r.GetInt32(2),
            });
        return list;
    }

    public void UnblockIp(string ip)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM IpBlocklist WHERE Ip = $ip";
        cmd.Parameters.AddWithValue("$ip", ip);
        cmd.ExecuteNonQuery();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        SqliteConnection conn = new SqliteConnection(connStr);
        conn.Open();
        conn.CreateCommand().ExecuteNonQuery(); // ensure WAL pragma
        using SqliteCommand pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static User ReadUser(SqliteDataReader r) => new()
    {
        Id                 = r.GetInt32(r.GetOrdinal("Id")),
        Username           = r.GetString(r.GetOrdinal("Username")),
        PasswordHash       = r.GetString(r.GetOrdinal("PasswordHash")),
        Role               = r.GetString(r.GetOrdinal("Role")),
        DisplayName        = r.GetString(r.GetOrdinal("DisplayName")),
        MustChangePassword = r.GetInt32(r.GetOrdinal("MustChangePassword")) == 1,
        CreatedAt          = r.GetInt64(r.GetOrdinal("CreatedAt")),
        LastActiveAt       = r.GetInt64(r.GetOrdinal("LastActiveAt")),
    };

    private static UserSession ReadSession(SqliteDataReader r) => new()
    {
        SessionId  = r.GetString(r.GetOrdinal("SessionId")),
        UserId     = r.GetInt32(r.GetOrdinal("UserId")),
        ExpiresAt  = r.GetInt64(r.GetOrdinal("ExpiresAt")),
        DeviceHint = r.GetString(r.GetOrdinal("DeviceHint")),
        IsDesktop  = r.GetInt32(r.GetOrdinal("IsDesktop")) == 1,
        LastUsedAt = r.GetInt64(r.GetOrdinal("LastUsedAt")),
    };
}
