namespace ARI.API;

/// <summary>The one way AriConfig.json is written: whole file to a temporary name first, then swapped in, one writer at a time,
/// so a crash or two quick saves can never leave a half-written file.</summary>
public static class ConfigFile
{
    private static readonly object Gate = new();

    public static void Write(string path, string contents)
    {
        lock (Gate)
        {
            string temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, contents);
            System.IO.File.Move(temp, path, overwrite: true);
        }
    }
}
