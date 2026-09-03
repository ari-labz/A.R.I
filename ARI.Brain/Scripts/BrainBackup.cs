using System.IO.Compression;
using System.Text.Json;

namespace ARI.BrainVault;

public record BackupInfo(string FileName, DateTime Created, long SizeBytes, int NoteCount);

// Snapshot/restore for the whole vault — a separate concern from reading and writing individual
// notes, split out of Brain for the same reason GraphMaintenance is: it's a real, distinct job.
public static class BrainBackup
{
    private const string BACKUP_PREFIX = "ARI-Brain-";

    internal static string Path_ = "./Backups";   // set by Brain.Initialize
    internal static int MaxBackups = 5;

    private record BackupNote(string Title, string Folder, string Content, IReadOnlyList<string>? Aliases, IReadOnlyList<string>? Keywords);

    public static string Backup()
    {
        Directory.CreateDirectory(Path_);
        List<BackupNote> notes = Database.AllNotes()
            .Select(note => new BackupNote(note.Title, note.Folder, note.ToPrompt(), note.Aliases, note.Keywords.Count > 0 ? note.Keywords : null))
            .ToList();

        string json = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, noteCount = notes.Count, notes },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        string fileName = $"{BACKUP_PREFIX}{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss}.zip";
        string zipPath = System.IO.Path.Combine(Path_, fileName);
        using (FileStream stream = File.Create(zipPath))
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
        using (StreamWriter writer = new(zip.CreateEntry("brain.json").Open()))
            writer.Write(json);

        List<string> existing = Directory.EnumerateFiles(Path_, $"{BACKUP_PREFIX}*.zip").OrderByDescending(f => f).ToList();
        foreach (string stale in existing.Skip(MaxBackups)) File.Delete(stale);
        return $"Backed up {notes.Count} notes to {fileName}.";
    }

    public static List<BackupInfo> ListBackups()
    {
        if (!Directory.Exists(Path_)) return new List<BackupInfo>();
        List<BackupInfo> backups = new();
        foreach (string file in Directory.EnumerateFiles(Path_, $"{BACKUP_PREFIX}*.zip").OrderByDescending(f => f))
        {
            using FileStream stream = File.OpenRead(file);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);
            using StreamReader reader = new(zip.GetEntry("brain.json")!.Open());
            JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            backups.Add(new BackupInfo(System.IO.Path.GetFileName(file), File.GetCreationTimeUtc(file),
                new FileInfo(file).Length, document.RootElement.GetProperty("noteCount").GetInt32()));
        }
        return backups;
    }

    // Additive: recreates missing notes, overwrites existing ones, never deletes.
    public static string RestoreBackup(string fileName)
    {
        string zipPath = System.IO.Path.Combine(Path_, fileName);
        using FileStream stream = File.OpenRead(zipPath);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        using StreamReader reader = new(zip.GetEntry("brain.json")!.Open());
        JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());

        int restored = 0;
        foreach (JsonElement element in document.RootElement.GetProperty("notes").EnumerateArray())
        {
            string title = element.GetProperty("title").GetString()!;
            string folder = element.GetProperty("folder").GetString() ?? string.Empty;
            string content = element.GetProperty("content").GetString()!;
            List<string> aliases = element.TryGetProperty("aliases", out JsonElement aliasElement) && aliasElement.ValueKind == JsonValueKind.Array
                ? aliasElement.EnumerateArray().Select(a => a.GetString()!).ToList()
                : new List<string>();
            List<string> keywords = element.TryGetProperty("keywords", out JsonElement kwElement) && kwElement.ValueKind == JsonValueKind.Array
                ? kwElement.EnumerateArray().Select(k => k.GetString()!).ToList()
                : new List<string>();

            int bodyStart = content.StartsWith("Path: ") ? content.IndexOf('\n') + 1 : 0;
            Note.Write(Brain.PathFor(folder.Length > 0 ? $"{folder}/{title}" : title), content[bodyStart..].TrimStart('\n'), aliases, null, keywords: keywords.Count > 0 ? keywords : null);
            restored++;
        }
        Brain.Index();
        return $"Restored {restored} notes from {fileName}.";
    }
}
