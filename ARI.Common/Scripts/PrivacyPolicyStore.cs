namespace ARI.Common;

public static class PrivacyPolicyStore
{
    private static readonly string FilePath = Path.Combine(Paths.PersistentData, "PrivacyPolicy.md");
    private static readonly object Lock = new();

    public static string Get()
    {
        lock (Lock)
        {
            try
            {
                if (File.Exists(FilePath)) return File.ReadAllText(FilePath);
                return "";
            }
            catch { return ""; }
        }
    }
}
