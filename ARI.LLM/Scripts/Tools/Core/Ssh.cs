using System.Diagnostics;
using ARI.Common;
using System.Text;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>The machines the owner has let Ari log in to. Kept in SshHosts.json in the server's data folder: a name, an address, a user, a port and the path of
/// a private key file. Ari only ever names a host; she never sees the key, and she can only reach hosts on this list.</summary>
internal static class SshHosts
{
    internal sealed record Host(string Name, string Address, string User, int Port, string? KeyFile);

    private static string File_ => System.IO.Path.Combine(Paths.AppData, "SshHosts.json");

    internal static List<Host> All()
    {
        try { return File.Exists(File_) ? JsonSerializer.Deserialize<List<Host>>(File.ReadAllText(File_)) ?? new() : new(); }
        catch { return new(); }
    }

    internal static Host? Find(string name) => All().FirstOrDefault(h => h.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static void Add(Host host)
    {
        List<Host> hosts = All().Where(h => !h.Name.Equals(host.Name, StringComparison.OrdinalIgnoreCase)).Append(host).ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Remove(string name)
    {
        List<Host> hosts = All().Where(h => !h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        File.WriteAllText(File_, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static List<string> Options(Host h, bool scp)
    {
        List<string> o = new() { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10", scp ? "-P" : "-p", h.Port.ToString() };
        if (!string.IsNullOrWhiteSpace(h.KeyFile)) { o.Add("-i"); o.Add(h.KeyFile.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))); }
        return o;
    }
}

[Tool("ssh", "Run a command on one of the user's other machines over SSH, or copy a file to or from it. Only machines the owner has saved can be used: list shows them with what they are. " +
             "run executes a command there (you are asked to approve every command), upload copies a file from your working folder to the machine, download copies one back into your working folder. " +
             "Logins use the saved key, so a machine that asks for a password can't be reached. To add a machine use add_host; the key file is never shown to you.", Group = "ssh_tools", TimeoutSeconds = 700)]
public class Ssh : Tool
{
    [Param("What to do.", Options = "list,run,upload,download,add_host,remove_host")]
    public string Action;

    [Param("The saved machine's name, e.g. spark. Not needed for list.", Optional = true)]
    public string Host;

    [Param("For run: the command to run on the machine.", Optional = true)]
    public string Command;

    [Param("For upload: the file in your working folder. For download: the file's path on the machine.", Optional = true)]
    public string Source;

    [Param("For upload: where to put it on the machine. For download: where to save it in your working folder (defaults to the file's own name).", Optional = true)]
    public string Dest;

    [Param("For add_host: the machine's address, e.g. 192.168.1.20 or box.local.", Optional = true)]
    public string Address;

    [Param("For add_host: the user to log in as.", Optional = true)]
    public string User;

    [Param("For add_host: the SSH port. Default 22.", Optional = true)]
    public int Port = 22;

    [Param("For add_host: the path of the private key file on this server, e.g. ~/.ssh/id_ed25519. Leave out to use the default keys.", Optional = true)]
    public string KeyFile;

    [Param("Give up after this many seconds. Default 60, at most 600.", Optional = true)]
    public int TimeoutSeconds = 60;

    public override string OnValidate() => IsAdmin ? null : "Only the owner, chatting in the app, can use SSH.";

    public override async Task<string> Run()
    {
        string action = (Action ?? "").Trim().ToLowerInvariant();
        if (action == "list")
        {
            List<SshHosts.Host> all = SshHosts.All();
            return all.Count == 0 ? "No machines are saved yet. Use add_host with a name, address and user (and a key file if it needs one)." : string.Join("\n", all.Select(h => $"{h.Name}: {h.User}@{h.Address}:{h.Port}"));
        }
        if (action == "add_host")
        {
            if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Address) || string.IsNullOrWhiteSpace(User)) return "[Error: add_host needs host (a name), address and user.]";
            string name = new(Host.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (name.Length == 0 || Address.Any(char.IsWhiteSpace) || Address.StartsWith('-') || User.Any(char.IsWhiteSpace) || User.StartsWith('-')) return "[Error: that name, address or user isn't valid.]";
            if (await ShellGate.Approve(CallThread, "Save an SSH machine", $"{name} = {User}@{Address}:{Port}" + (string.IsNullOrWhiteSpace(KeyFile) ? "" : $" (key {KeyFile})"), allowListed: false) is { } no) return no;
            SshHosts.Add(new SshHosts.Host(name, Address.Trim(), User.Trim(), Port, string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim()));
            return $"Saved {name}. Try it with run and a simple command like \"hostname\".";
        }
        if (action == "remove_host")
        {
            if (SshHosts.Find(Host) is not { } gone) return $"[Error: there is no saved machine called {Host}.]";
            SshHosts.Remove(gone.Name); return $"Removed {gone.Name}.";
        }

        if (SshHosts.Find(Host) is not { } h)
            return $"[Error: there is no saved machine called \"{Host}\". Saved: {(SshHosts.All().Count == 0 ? "none" : string.Join(", ", SshHosts.All().Select(x => x.Name)))}.]";
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 600));

        switch (action)
        {
            case "run":
            {
                if (string.IsNullOrWhiteSpace(Command)) return "[Error: give the command to run.]";
                if (await ShellGate.Approve(CallThread, $"Run on {h.Name} ({h.User}@{h.Address})", Command, allowListed: false) is { } refusal) return refusal;
                ProcessStartInfo psi = ProcessRunner.Prepare(new ProcessStartInfo("ssh"), WorkingFolder);
                foreach (string o in SshHosts.Options(h, scp: false)) psi.ArgumentList.Add(o);
                psi.ArgumentList.Add($"{h.User}@{h.Address}"); psi.ArgumentList.Add(Command);
                return await Exec(psi, timeout, h);
            }
            case "upload":
            {
                if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Dest)) return "[Error: give source (a file in your working folder) and dest (a path on the machine).]";
                string local = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Source) ? Source : System.IO.Path.Combine(WorkingFolder, Source));
                if (!File.Exists(local)) return $"[Error: {local} doesn't exist.]";
                if (await ShellGate.Approve(CallThread, $"Copy a file to {h.Name}", $"{local} -> {h.User}@{h.Address}:{Dest}", allowListed: false) is { } refusal) return refusal;
                ProcessStartInfo psi = ProcessRunner.Prepare(new ProcessStartInfo("scp"), WorkingFolder);
                foreach (string o in SshHosts.Options(h, scp: true)) psi.ArgumentList.Add(o);
                psi.ArgumentList.Add(local); psi.ArgumentList.Add($"{h.User}@{h.Address}:{Dest}");
                string r = await Exec(psi, timeout, h);
                return r.StartsWith("[") ? r : $"Copied {System.IO.Path.GetFileName(local)} to {h.Name}:{Dest}.";
            }
            case "download":
            {
                if (string.IsNullOrWhiteSpace(Source)) return "[Error: give source (the file's path on the machine).]";
                string leaf = string.IsNullOrWhiteSpace(Dest) ? Source.Replace('\\', '/').Split('/').Last() : Dest;
                string full = SaveRules.Resolve(WorkingFolder, leaf, System.IO.Path.GetExtension(leaf), CallThread, out string? blocked);
                if (blocked is not null) return blocked;
                if (await ShellGate.Approve(CallThread, $"Copy a file from {h.Name}", $"{h.User}@{h.Address}:{Source} -> {full}", allowListed: false) is { } refusal) return refusal;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                ProcessStartInfo psi = ProcessRunner.Prepare(new ProcessStartInfo("scp"), WorkingFolder);
                foreach (string o in SshHosts.Options(h, scp: true)) psi.ArgumentList.Add(o);
                psi.ArgumentList.Add($"{h.User}@{h.Address}:{Source}"); psi.ArgumentList.Add(full);
                string r = await Exec(psi, timeout, h);
                if (r.StartsWith("[")) return r;
                SaveRules.Created(CallThread, full);
                return $"Saved {full} ({new FileInfo(full).Length:N0} bytes). Give it to the user with deliver_file, or read it with read_file.";
            }
            default: return "[Error: action must be list, run, upload, download, add_host or remove_host.]";
        }
    }

    private async Task<string> Exec(ProcessStartInfo psi, TimeSpan timeout, SshHosts.Host h)
    {
        try
        {
            ProcessRunner.Result r = await ProcessRunner.Run(psi, timeout, Cancelled);
            string text = ProcessRunner.Format(r);
            if (text.Contains("Permission denied") || text.Contains("Host key verification failed") || text.Contains("Connection refused") || text.Contains("Could not resolve"))
                text += $"\n[Couldn't log in to {h.Name}. It needs a working key for {h.User}@{h.Address}:{h.Port} (passwords aren't used); the owner can check the key file and that the machine is on.]";
            return text;
        }
        catch (System.ComponentModel.Win32Exception) { return "[Error: ssh isn't installed on this server.]"; }
    }
}
