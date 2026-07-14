namespace ARI.Common;

/// <summary>Locates the phonemization overrides file (PhonemeSubstitutions.json) that ships next
/// to the ARI executable. This file is owned by the ARI project and its path is passed to the
/// StyleTTS2 scripts — it is never copied into the submodule.</summary>
public static class PhonemeSubstitutions
{
    public static string? Path
    {
        get
        {
            string p = System.IO.Path.Combine(Paths.BuildPath, "PhonemeSubstitutions.json");
            return System.IO.File.Exists(p) ? p : null;
        }
    }
}
