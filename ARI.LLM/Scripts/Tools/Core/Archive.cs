using System.IO.Compression;
using System.Text;

namespace ARI.LLM;

[Tool("archive", "Work with .zip files in your working folder. list shows what's inside one; extract unpacks it into a folder; create makes a zip from files or folders (one per line). " +
                 "Give a zip you made to the user with deliver_file. A zip that was there before the conversation is never overwritten. For tar.gz and other formats use the shell.", Group = "filesystem_tools")]
public class Archive : Tool
{
    [Param("What to do.", Options = "list,extract,create")]
    public string Action;

    [Param("The zip file, ending in .zip.")]
    public string Path;

    [Param("For create: the files and folders to put in, one per line, relative to your working folder.", Optional = true)]
    public string Files;

    [Param("For extract: the folder to unpack into. Defaults to a folder named after the zip.", Optional = true)]
    public string Dest;

    private string Full(string p) => System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(WorkingFolder, p));

    private bool Inside(string full) => full.StartsWith(System.IO.Path.GetFullPath(WorkingFolder) + System.IO.Path.DirectorySeparatorChar) || full == System.IO.Path.GetFullPath(WorkingFolder);

    public override Task<string> Run()
    {
        string zip = Full(Path);
        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Task.FromResult("[Error: the file name must end in .zip.]");
        switch ((Action ?? "").Trim().ToLowerInvariant())
        {
            case "list":
            {
                if (!File.Exists(zip)) return Task.FromResult($"[Error: {zip} doesn't exist.]");
                using ZipArchive z = ZipFile.OpenRead(zip);
                StringBuilder sb = new(); sb.AppendLine($"{z.Entries.Count} entries:");
                foreach (ZipArchiveEntry e in z.Entries.Take(200)) sb.AppendLine($"  {e.FullName}  ({e.Length:N0} bytes)");
                if (z.Entries.Count > 200) sb.AppendLine($"  … and {z.Entries.Count - 200} more");
                return Task.FromResult(sb.ToString());
            }
            case "extract":
            {
                if (!File.Exists(zip)) return Task.FromResult($"[Error: {zip} doesn't exist.]");
                string dest = Full(string.IsNullOrWhiteSpace(Dest) ? System.IO.Path.GetFileNameWithoutExtension(zip) : Dest);
                if (!Inside(dest)) return Task.FromResult("[Error: unpack inside your working folder.]");
                using ZipArchive z = ZipFile.OpenRead(zip);
                if (z.Entries.Sum(e => e.Length) > 500L * 1024 * 1024) return Task.FromResult("[Error: that zip unpacks to over 500 MB, which is more than I'll extract.]");
                int n = 0, skipped = 0;
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dest, e.FullName));
                    if (!target.StartsWith(System.IO.Path.GetFullPath(dest) + System.IO.Path.DirectorySeparatorChar)) { skipped++; continue; }   // a path trying to climb out
                    if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    if (File.Exists(target) && CallThread is not null && !CallThread.CreatedFiles.Contains(target)) { skipped++; continue; }   // the user's own file
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true); SaveRules.Created(CallThread, target); n++;
                }
                return Task.FromResult($"Unpacked {n} file{(n == 1 ? "" : "s")} into {dest}." + (skipped > 0 ? $" Skipped {skipped} (unsafe paths or files already there from before)." : ""));
            }
            case "create":
            {
                string? refusal = null; zip = SaveRules.Resolve(WorkingFolder, Path, ".zip", CallThread, out refusal);
                if (refusal is not null) return Task.FromResult(refusal);
                List<string> items = (Files ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (items.Count == 0) return Task.FromResult("[Error: say which files or folders to put in the zip, one per line.]");
                string root = System.IO.Path.GetFullPath(WorkingFolder);
                int count = 0; long bytes = 0;
                if (File.Exists(zip)) File.Delete(zip);
                using (ZipArchive z = ZipFile.Open(zip, ZipArchiveMode.Create))
                    foreach (string item in items)
                    {
                        string full = Full(item);
                        if (!Inside(full)) return Task.FromResult($"[Error: {item} is outside your working folder.]");
                        IEnumerable<string> found = Directory.Exists(full) ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories) : File.Exists(full) ? new[] { full } : Array.Empty<string>();
                        if (!found.Any()) return Task.FromResult($"[Error: nothing found at {item}.]");
                        foreach (string f in found)
                        {
                            if (System.IO.Path.GetFullPath(f) == zip) continue;
                            bytes += new FileInfo(f).Length;
                            if (bytes > 500L * 1024 * 1024) return Task.FromResult("[Error: that's over 500 MB, which is more than I'll zip.]");
                            z.CreateEntryFromFile(f, System.IO.Path.GetRelativePath(root, f).Replace('\\', '/')); count++;
                        }
                    }
                SaveRules.Created(CallThread, zip);
                return Task.FromResult($"Saved {zip} ({count} file{(count == 1 ? "" : "s")}, {new FileInfo(zip).Length:N0} bytes). Give it to the user with deliver_file.");
            }
            default: return Task.FromResult("[Error: action must be list, extract or create.]");
        }
    }
}
