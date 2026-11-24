namespace ARI.Common;

/// <summary>
/// The Python a voice module runs on. A module states its version in a .python-version file at its root (see
/// Voice/Module-Contract); one that doesn't say gets <see cref="FALLBACK_VERSION"/>, which every current module supports.
/// The interpreter is downloaded by <see cref="PythonRuntime"/>, never taken from the machine.
/// </summary>
public static class VoiceModulePython
{
    public const string FALLBACK_VERSION = "3.11";

    public static string VersionFor(string moduleDir) => PythonRuntime.DeclaredVersion(moduleDir, FALLBACK_VERSION);

    public static Task<string> For(string moduleDir, CancellationToken ct = default) => PythonRuntime.Ensure(VersionFor(moduleDir), ct);
}
