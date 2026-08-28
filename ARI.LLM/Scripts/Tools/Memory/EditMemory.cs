using System.Diagnostics;
using System.Text.Json;
using ARI.Brain;

namespace ARI.LLM;

/// <summary>
/// Edits a brain note by line number — identical semantics to edit_file but commits to the brain
/// git repo immediately so every correction is reversible. Use ONLY to fix incorrect information
/// in memory: wrong facts, outdated details, inconsistent entries. Never add new content here —
/// that belongs to Engram. Never delete notes.
/// </summary>
internal sealed class EditMemory : Tool
{
    internal override string Name => "edit_memory";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "edit_memory",
            description = "Correct a mistake in a brain note. Use ONLY to fix information that is wrong or inconsistent — not to add new content (that is Engram's job). Edits by line number, same as edit_file. Call recall_memory first so you have the current line numbers. On success, the change is committed to the brain git repo immediately so it can be rolled back if needed.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path         = new { type = "string",  description = "Note path relative to the brain vault root (e.g. 'People/Xywren.md'), as returned by search_brain or recall_memory." },
                    start_line   = new { type = "integer", description = "REPLACE mode: first line to replace (1-based inclusive). Omit when inserting." },
                    end_line     = new { type = "integer", description = "REPLACE mode: last line to replace (1-based inclusive). Equals start_line for a single-line change. Omit when inserting." },
                    insert_after = new { type = "integer", description = "INSERT mode: add new_string immediately after this 1-based line (0 = top of file), replacing nothing." },
                    new_string   = new { type = "string",  description = "Replacement or inserted line(s). Empty string in REPLACE mode deletes the range." },
                    edits        = new { type = "array",   description = "Multiple changes at once. Each item is a replace {start_line,end_line,new_string} or insert {insert_after,new_string}. Resolve against the file as last read." },
                    commit_message = new { type = "string", description = "One-line summary of what was corrected and why (e.g. 'Fix pronouns in Xywren note — confirmed he/him')." }
                },
                required = new[] { "path", "commit_message" }
            }
        }
    };

    internal override string? PreCheck(Thread thread, string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.TryGetProperty("path", out JsonElement p) && p.GetString() is { } path)
            {
                string full = System.IO.Path.Combine(BrainModule.VaultRoot, path);
                if (!System.IO.File.Exists(full))
                    return $"[Blocked] '{path}' not found in the brain vault. Use search_brain to find the correct path, then recall_memory to read it before editing.";
            }
        }
        catch { }
        return null;
    }

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        if (!BrainModule.Ready)
            return "Brain is not available right now.";

        JsonElement root;
        string path, commitMessage;
        try
        {
            root          = JsonDocument.Parse(argsJson).RootElement;
            path          = root.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
            commitMessage = root.TryGetProperty("commit_message", out JsonElement cm) ? cm.GetString() ?? "" : "";
        }
        catch { return "Error: could not parse arguments."; }

        if (path.Length == 0)          return "Error: 'path' is required.";
        if (commitMessage.Length == 0) return "Error: 'commit_message' is required.";

        string vaultRoot = BrainModule.VaultRoot;
        ServerFileSystem fs = new(vaultRoot, CancellationToken.None, brainVault: true);

        // Rewrite args so the path resolves against the vault root (fs.Edit expects project-relative paths)
        string editResult = await fs.Edit(argsJson);
        if (editResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
            editResult.StartsWith("[", StringComparison.OrdinalIgnoreCase))
            return editResult;

        // Commit the change to the brain repo
        (int _, string status, string _) = Run(vaultRoot, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status))
            return $"{editResult}\n(No git changes detected — file may be unchanged.)";

        Run(vaultRoot, "add", "-A");
        (int code, string _, string err) = RunInput(vaultRoot, commitMessage, "commit", "-F", "-");
        if (code != 0) return $"{editResult}\nCommit failed: {err.Trim()}";

        (int _, string head, string _) = Run(vaultRoot, "log", "-1", "--format=%h %s");
        BrainModule.Index();
        return $"{editResult}\nCommitted to brain: {head.Trim()}";
    }

    private static (int Code, string Out, string Err) RunInput(string workDir, string? stdin, params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName               = "git",
            WorkingDirectory       = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = stdin is not null,
            UseShellExecute        = false,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi)!;
        if (stdin is not null) { process.StandardInput.Write(stdin); process.StandardInput.Close(); }
        string outp = process.StandardOutput.ReadToEnd();
        string err  = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, outp, err);
    }

    private static (int Code, string Out, string Err) Run(string workDir, params string[] args)
        => RunInput(workDir, null, args);
}
