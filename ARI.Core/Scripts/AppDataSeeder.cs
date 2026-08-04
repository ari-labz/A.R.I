using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.Core;

/// <summary>
/// Copies missing files and folders from AppDataDefaults into AppData on first run.
/// Files that already exist are never touched — user data is never overwritten.
/// </summary>
public static class AppDataSeeder
{
    public static void Seed(string defaultsDir, string appDataDir)
    {
        if (!Directory.Exists(defaultsDir))
        {
            Shared.Logger.LogWarning("[AppDataSeeder] Defaults directory not found at {Path} — skipping seed.", defaultsDir);
            return;
        }

        Shared.Logger.LogInformation("[AppDataSeeder] Seeding app data...");

        int seeded = 0;
        foreach (string sourcePath in Directory.EnumerateFiles(defaultsDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(defaultsDir, sourcePath);
            string destPath = Path.Combine(appDataDir, relative);

            if (File.Exists(destPath))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(sourcePath, destPath);
            Shared.Logger.LogInformation("[AppDataSeeder] Seeded {File}", relative);
            seeded++;
        }

        Shared.Logger.LogInformation("[AppDataSeeder] Seeding complete ({Count} file(s) added).", seeded);
    }
}
