using System.Text.Json;

namespace ARI.LLM;

// Generic git tools. Registered against a repo root; usable by any agent. The memory agents use them
// to read a note's history before editing (loop-prevention) and to commit one logical change at a time.

file static class GitArgs
{
    internal static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }
    internal static string Str(this JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    internal static int Int(this JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}

internal sealed class GitStatus : GitTool
{
    internal GitStatus(string root) : base(root) { }
    internal override string Name => "git_status";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git_status",
            description = "Show the working-tree status (which files are changed, added, or deleted since the last commit).",
            parameters  = new { type = "object", properties = new { } }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        (int _, string outp, string err) = Run("status", "--short");
        if (err.Length > 0 && outp.Length == 0) return Task.FromResult($"git status failed: {err.Trim()}");
        return Task.FromResult(string.IsNullOrWhiteSpace(outp) ? "Working tree clean." : outp.TrimEnd());
    }
}

internal sealed class GitDiff : GitTool
{
    internal GitDiff(string root) : base(root) { }
    internal override string Name => "git_diff";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git_diff",
            description = "Show the uncommitted working-tree diff — all changes, or just one path. Always review the diff before committing.",
            parameters  = new
            {
                type       = "object",
                properties = new { path = new { type = "string", description = "Optional path to limit the diff to." } }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string path = GitArgs.Parse(argsJson).Str("path");
        (int _, string outp, string err) = path.Length > 0 ? Run("diff", "--", path) : Run("diff");
        if (err.Length > 0 && outp.Length == 0) return Task.FromResult($"git diff failed: {err.Trim()}");
        return Task.FromResult(string.IsNullOrWhiteSpace(outp) ? "No uncommitted changes." : outp.TrimEnd());
    }
}

internal sealed class GitLog : GitTool
{
    internal GitLog(string root) : base(root) { }
    internal override string Name => "git_log";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git_log",
            description = "Show recent commit history with full messages, optionally for one path. Read a file's history BEFORE editing it to see why it was last changed and whether you'd be undoing your own earlier work.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    path = new { type = "string", description = "Optional path to limit history to." },
                    max  = new { type = "integer", description = "How many commits to show (default 10)." }
                }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        JsonElement a = GitArgs.Parse(argsJson);
        string path = a.Str("path");
        int max = a.Int("max", 10);
        (int code, string outp, string err) = path.Length > 0
            ? Run("log", $"-n{max}", "--format=%h %ad%n%B", "--date=short", "--", path)
            : Run("log", $"-n{max}", "--format=%h %ad%n%B", "--date=short");
        if (code != 0) return Task.FromResult($"git log failed: {err.Trim()}");
        return Task.FromResult(string.IsNullOrWhiteSpace(outp) ? "No history." : outp.TrimEnd());
    }
}

internal sealed class GitCommit : GitTool
{
    private readonly string? coAuthor;   // e.g. "A.R.I <ari@ari.local>" → appended as a Co-Authored-By trailer
    internal GitCommit(string root, string? coAuthor = null) : base(root) => this.coAuthor = coAuthor;
    internal override string Name => "git_commit";
    internal override object Schema => new
    {
        type = "function",
        function = new
        {
            name        = "git_commit",
            // ONE parameter on purpose: the model reliably merges separate subject/body fields into one and
            // adds stray quotes, which breaks the text tool-call parser. A single `message` field removes that
            // whole failure class. First line = what changed; leave a blank line then the why.
            description = "Stage all changes and commit them. One commit per logical change. Provide a single 'message': the first line is a short summary of WHAT changed, then a blank line, then WHY. Review the diff first.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    message = new { type = "string", description = "Commit message: first line = what changed; blank line; then why." }
                },
                required = new[] { "message" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        JsonElement a = GitArgs.Parse(argsJson);
        string message = a.Str("message");
        if (message.Length == 0) return Task.FromResult("Error: 'message' is required.");

        (int _, string status, string _) = Run("status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status)) return Task.FromResult("Nothing to commit — working tree clean.");

        Run("add", "-A");
        if (!string.IsNullOrWhiteSpace(coAuthor)) message += $"\n\nCo-Authored-By: {coAuthor}";
        (int code, string _, string err) = RunInput(message, "commit", "-F", "-");
        if (code != 0) return Task.FromResult($"Commit failed: {err.Trim()}");

        (int _, string head, string _) = Run("log", "-1", "--format=%h %s");
        return Task.FromResult($"Committed {head.Trim()}");
    }
}
