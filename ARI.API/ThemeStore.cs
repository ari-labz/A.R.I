using System.Text.Json;
using System.Text.RegularExpressions;
using ARI.Common;

namespace ARI.API;

/// <summary>Colour overrides chosen in the control panel: colour name (a key in ARI.UI/public/colours.json)
/// to a CSS colour. Anything not overridden keeps the value from colours.json.</summary>
public sealed class Theme
{
    public Dictionary<string, string> Overrides { get; set; } = new();
}

public static class ThemeStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "Theme.json");
    private static readonly object Lock = new();
    private static readonly Regex Name = new("^[a-z0-9-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex Colour = new(
        @"^(#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?|rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(,\s*(0|1|0?\.\d+|1\.0+)\s*)?\))$",
        RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Theme Get()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Theme>(File.ReadAllText(FilePath), Opts) is { } t)
                    return new Theme { Overrides = Clean(t.Overrides) };
            }
            catch { }
            return new Theme();
        }
    }

    /// <summary>Returns an error message, or null when saved.</summary>
    public static string? Set(Theme t)
    {
        foreach ((string k, string v) in t.Overrides ?? [])
        {
            if (!Name.IsMatch(k)) return $"'{k}' is not a valid colour name.";
            if (!Colour.IsMatch(v ?? "")) return $"'{k}': {v} is not a colour (use #RRGGBB or rgba(r,g,b,a)).";
        }
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Theme { Overrides = Clean(t.Overrides) }, Opts));
        }
        return null;
    }

    public static void Reset()
    {
        lock (Lock) { if (File.Exists(FilePath)) File.Delete(FilePath); }
    }

    // A hand-edited file with bad entries just drops those entries rather than breaking the UI.
    private static Dictionary<string, string> Clean(Dictionary<string, string>? src) =>
        (src ?? []).Where(kv => Name.IsMatch(kv.Key) && Colour.IsMatch(kv.Value ?? "")).ToDictionary(kv => kv.Key, kv => kv.Value);
}
