namespace ARI.Common;

/// <summary>
/// Single source of truth for every on-disk location ARI resolves. Nothing outside this class
/// should construct one of these paths by hand — retargeting any of them (a different data drive,
/// a different install location, an external model library) means editing here only.
///
///   AppData   — persistent data. Survives rebuilds/updates. ~/ARI (mac/linux) or %APPDATA%\ARI
///               (windows) — same folder name on both. Env override: APP_DATA_ROOT.
///   BuildPath — where the build lands. Contains only build output, no user data.
///               /Applications/A.R.I (mac) or %ProgramFiles%\A.R.I (windows).
///               Env override: APP_INSTALL_ROOT. This matches ARI.Core.csproj's OutputPath, so
///               the running assembly's own directory is always BuildPath — dev and installed
///               runs are indistinguishable from here on, no dev-vs-install branching needed.
///   Models    — large, often shared with other tools, so it's split out of AppData rather than
///               nested under it — lets a user point ARI at a model library they already have.
///               Env override: MODELS_PATH.
/// </summary>
public static class Paths
{
    public static string AppData   { get; }
    public static string BuildPath { get; }
    public static string Models    { get; }

    // Server-side persistent data — AppData/Server/...
    public static string PersistentData { get; }
    public static string Voices         { get; }
    public static string Brain          { get; }
    public static string Logs           { get; }
    public static string ChatHistory    { get; }
    public static string Keys           { get; }
    public static string Push           { get; }
    public static string LLMConfigs     { get; }

    // StyleTTS2's mutable state (venv, per-model training work dirs, the downloaded pretrained
    // checkpoint cache) — never lives under StyleTts2Source, which is install content.
    public static string StyleTts2Data { get; }

    // Listener's mutable state (its faster-whisper venv) — never lives under ListenerScript's
    // directory, which is install content.
    public static string ListenerData { get; }

    // Python venvs live at a SHORT path (…/ARI/venvs/*), managed and auto-installed by ARI (like
    // llama.cpp's tools dir), rather than buried under the deep StyleTts2Data / ListenerData trees —
    // otherwise nested package paths (e.g. torch's license files) blow past Windows' 260-char limit.
    public static string StyleTts2Venv    { get; }
    public static string StyleTts2Python  { get; }
    public static string StyleTts2Whisper { get; }
    public static string ListenerVenv     { get; }
    public static string ListenerPython   { get; }

    // Config + secrets — never in BuildPath, which may be wiped/replaced wholesale on update.
    // AriConfig.json is seeded from the bundled default (see AriConfig.Load) the first time it's
    // missing here, then edited in place — so instance customization survives updates. secrets.env
    // is the single source for every ${PLACEHOLDER} AriConfig.json references (e.g. the Discord
    // token) — read by AriConfig's placeholder substitution.
    public static string AriConfig { get; }
    public static string Secrets   { get; }

    // Client-side persistent data — AppData/Client
    public static string ClientData { get; }

    // Install content — always BuildPath-relative (see the Content-copy items in
    // ARI.Core.csproj). These are read-only from the app's perspective; nothing under here is
    // ever written to at runtime.
    public static string WwwRoot             { get; }
    public static string StyleTts2Source     { get; }
    public static string ListenerScript      { get; }
    public static string ListenerRequirements { get; }

    static Paths()
    {
        string? dataOverride = Environment.GetEnvironmentVariable("APP_DATA_ROOT");
        if (dataOverride is null)
        {
            switch (0)
            {
                case 0 when OperatingSystem.IsWindows():
                    AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ARI");
                    break;
                case 0 when OperatingSystem.IsMacOS():
                    AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ARI");
                    break;
                case 0 when OperatingSystem.IsLinux():
                default:
                    AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ARI");
                    break;
            }
        }
        else
        {
            AppData = dataOverride;
        }

        string? installOverride = Environment.GetEnvironmentVariable("APP_INSTALL_ROOT");
        if (installOverride is not null)
        {
            BuildPath = installOverride;
        }
        else if (TryFindDevBuildRoot() is { } devBuild)
        {
            // Running from a source checkout (Rider / `dotnet run`): keep build output in the repo's
            // devbuild/ folder so /Applications (and %ProgramFiles%) stay reserved for real installs.
            BuildPath = devBuild;
        }
        else
        {
            switch (0)
            {
                case 0 when OperatingSystem.IsWindows():
                    BuildPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "A.R.I");
                    break;
                case 0 when OperatingSystem.IsMacOS():
                    BuildPath = "/Applications/A.R.I";
                    break;
                case 0 when OperatingSystem.IsLinux():
                default:
                    BuildPath = "/opt/A.R.I";
                    break;
            }
        }

        string? modelsOverride = Environment.GetEnvironmentVariable("MODELS_PATH");
        if (!string.IsNullOrEmpty(modelsOverride))
        {
            Models = modelsOverride;
        }
        else
        {
            Models = Path.Combine(AppData, "Models");
        }

        // Config files (Servers, Models, Persona, Scheduler, Agents…) sit directly in the server
        // app-data root — there is no PersistentData subfolder.
        PersistentData = Path.Combine(AppData, "Server");
        Directory.CreateDirectory(PersistentData);
        Voices         = ServerDir("Voices");
        Brain          = ServerDir("Brain");
        Logs           = ServerDir("Logs");
        ChatHistory    = ServerDir("ChatHistory");
        Keys           = ServerDir("Keys");
        Push           = ServerDir("Push");
        LLMConfigs     = ServerDir("LLMConfigs");
        StyleTts2Data  = ServerDir("External/StyleTTS2");
        ListenerData   = ServerDir("External/Listener");

        StyleTts2Venv    = Path.Combine(AppData, "venvs", "stt");
        StyleTts2Python  = Path.Combine(StyleTts2Venv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");
        StyleTts2Whisper = Path.Combine(StyleTts2Venv, OperatingSystem.IsWindows() ? @"Scripts\whisper.exe" : "bin/whisper");
        ListenerVenv     = Path.Combine(AppData, "venvs", "listener");
        ListenerPython   = Path.Combine(ListenerVenv, OperatingSystem.IsWindows() ? @"Scripts\python.exe" : "bin/python");

        AriConfig = Path.Combine(AppData, "Server", "AriConfig.json");
        Secrets   = Path.Combine(AppData, "Server", "secrets.env");

        ClientData = Path.Combine(AppData, "Client");
        Directory.CreateDirectory(ClientData);

        WwwRoot         = Path.Combine(BuildPath, "wwwroot");
        StyleTts2Source = Path.Combine(BuildPath, "External", "StyleTTS2");
        ListenerScript       = Path.Combine(BuildPath, "Listener", "whisper_serve.py");
        ListenerRequirements = Path.Combine(BuildPath, "Listener", "requirements.txt");
    }

    /// <summary>When the app runs from a source checkout, returns "&lt;repoRoot&gt;/devbuild" (the dev
    /// OutputPath — see ARI.Core.csproj). Returns null for installed builds, where no ARI.sln sits
    /// above the executable, so the OS install location is used instead.</summary>
    private static string? TryFindDevBuildRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("ARI.sln").Length > 0)
                return Path.Combine(dir.FullName, "devbuild");
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Server-side persistent data subfolder not already exposed above (creates it if missing).</summary>
    public static string ServerDir(string sub)
    {
        string path = Path.Combine(AppData, "Server", sub);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Client-side persistent data subfolder not already exposed above (creates it if missing).</summary>
    public static string ClientDir(string sub)
    {
        string path = Path.Combine(ClientData, sub);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Resolves a possibly-relative override path against BuildPath; empty stays empty.</summary>
    public static string ResolveOverride(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BuildPath, path));
    }
}
