using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>How one command in a command line is treated.</summary>
public enum CommandTier
{
    /// <summary>Only reads (grep, cat, git status…): runs without asking.</summary>
    ReadOnly,
    /// <summary>Asks unless its key ("grep", "git commit", "dotnet build") is on the allowlist; "always" stores the key.</summary>
    Allowable,
    /// <summary>Destructive (git reset --hard, rm -rf, sudo…): may be allowed once, never stored, so it asks every time.</summary>
    Dangerous,
    /// <summary>Can't be checked safely (command substitution, bare git, unbalanced quotes): asks every time.</summary>
    Opaque,
}

/// <summary>One command in a command line: as written, its allowlist key (null when it can't be stored), and why it
/// is dangerous or opaque, for the prompt.</summary>
public sealed record CommandSegment(string Text, string? Key, CommandTier Tier, string? Reason = null, bool ThreadScoped = false);

/// <summary>
/// Whether a command may run without asking the owner, for the shell tool and the desktop app's run_command alike.
/// A command line is split into the commands it actually runs (pipes, &amp;&amp;, ||, ;, a bare &amp;), respecting quotes,
/// so chaining never slips a command past. Each one is read-only (runs), allowable (runs if its key is on the
/// allowlist), dangerous or opaque (asks every time). The line runs without a prompt only when every command does.
/// </summary>
public static class CommandPolicy
{
    // Programs whose first sub-word is the real command ("dotnet build", not all of "dotnet").
    public static readonly HashSet<string> Multiplexers = new(StringComparer.Ordinal)
        { "git", "dotnet", "npm", "yarn", "pnpm", "bun", "deno", "docker", "cargo", "go", "kubectl", "pip", "pip3", "brew", "make" };

    // Programs that only read, whatever their arguments (the write forms of a few are caught in WritesFiles).
    private static readonly HashSet<string> ReadOnlyPrograms = new(StringComparer.Ordinal)
    {
        "cd", "pwd", "ls", "echo", "printf", "cat", "head", "tail", "less", "more", "wc", "sort", "uniq", "cut", "tr", "nl",
        "column", "grep", "egrep", "fgrep", "rg", "ag", "sed", "find", "tree", "file", "stat", "du", "df", "which", "whereis",
        "type", "date", "whoami", "id", "uname", "hostname", "uptime", "ps", "env", "printenv", "basename", "dirname",
        "realpath", "readlink", "true", "false", "test", "diff", "cmp", "comm", "jq", "md5", "md5sum", "shasum", "sha1sum",
        "sha256sum", "hexdump", "od", "strings", "sw_vers", "lsof", "pgrep", "nproc", "free", "top",
    };

    // git subcommands that only read. (branch, stash, tag, remote, fetch, reflog and grep have write forms.)
    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "blame", "rev-parse", "ls-files", "ls-tree", "describe", "shortlog", "cat-file",
        "name-rev", "for-each-ref", "count-objects",
    };

    // Programs that are dangerous however they are called.
    private static readonly Dictionary<string, string> DangerousPrograms = new(StringComparer.Ordinal)
    {
        ["sudo"] = "runs with elevated privileges", ["su"] = "switches user", ["doas"] = "runs with elevated privileges",
        ["dd"] = "writes raw data to disks or files", ["fdisk"] = "edits disk partitions", ["diskutil"] = "manages disks",
        ["shutdown"] = "shuts the machine down", ["reboot"] = "reboots the machine", ["halt"] = "halts the machine",
        ["sh"] = "runs arbitrary shell code", ["bash"] = "runs arbitrary shell code", ["zsh"] = "runs arbitrary shell code",
        ["fish"] = "runs arbitrary shell code", ["eval"] = "runs arbitrary shell code", ["exec"] = "replaces the shell with another program",
    };

    private static readonly string[] TrustedBinDirs = { "/usr/bin/", "/bin/", "/usr/local/bin/", "/opt/homebrew/bin/" };
    private static readonly HashSet<string> GitValueOptions = new(StringComparer.Ordinal)
        { "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix", "--config-env" };

    // ── Parsing ───────────────────────────────────────────────────────────────────────────

    /// <summary>The commands a line runs, split at pipes, &amp;&amp;, ||, ;, newlines, a bare &amp; and subshell brackets, respecting
    /// quotes (a | inside quotes isn't a pipe, nor is the &amp; in 2&gt;&amp;1). A heredoc body is text, not commands. Whatever is
    /// inside $(…), backticks or &lt;(…) is returned in <paramref name="inner"/> to be checked as commands of their own.
    /// Null when the quotes or brackets don't balance, which can hide commands.</summary>
    public static List<string>? Segments(string command, out List<string> inner)
    {
        inner = new();
        string cmd = command.Trim();
        if (cmd.Length == 0) return null;
        List<string> segments = new();
        StringBuilder cur = new();
        char? quote = null;
        (string Word, bool Dash, bool Quoted)? heredoc = null;
        for (int i = 0; i < cmd.Length; i++)
        {
            char c = cmd[i];
            char next = i + 1 < cmd.Length ? cmd[i + 1] : '\0', prev = i > 0 ? cmd[i - 1] : '\0';
            if (quote != '\'' && c == '\\' && i + 1 < cmd.Length) { cur.Append(c).Append(next); i++; continue; }
            if (quote != '\'')
            {
                // Substitutions run their contents, even inside double quotes.
                int end = -1;
                if (c == '$' && next == '(' || (quote is null && c is '<' or '>' && next == '('))
                {
                    end = MatchingParen(cmd, i + 1);
                    if (end < 0) return null;
                    if (!(c == '$' && i + 2 < cmd.Length && cmd[i + 2] == '(')) inner.Add(cmd[(i + 2)..end]);   // $((1+2)) is arithmetic, not a command
                }
                else if (c == '`')
                {
                    end = cmd.IndexOf('`', i + 1);
                    if (end < 0) return null;
                    inner.Add(cmd[(i + 1)..end]);
                }
                if (end >= 0) { cur.Append(cmd, i, end - i + 1); i = end; continue; }
            }
            if (quote is not null) { cur.Append(c); if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; cur.Append(c); continue; }
            if (c == '<' && next == '<' && i + 2 < cmd.Length && cmd[i + 2] != '<' && prev != '<')
            {
                // <<WORD, <<-WORD, <<'WORD': the lines up to WORD are input to the command, not commands.
                int j = i + 2; bool dash = j < cmd.Length && cmd[j] == '-'; if (dash) j++;
                while (j < cmd.Length && cmd[j] == ' ') j++;
                int start = j; bool quoted = false;
                while (j < cmd.Length && !char.IsWhiteSpace(cmd[j]) && cmd[j] is not (';' or '|' or '&' or ')' or '>' or '<'))
                { if (cmd[j] is '\'' or '"' or '\\') quoted = true; j++; }
                string word = cmd[start..j].Trim('\'', '"', '\\');
                if (word.Length > 0) heredoc = (word, dash, quoted);
                cur.Append(cmd, i, j - i); i = j - 1; continue;
            }
            if (c == '\n' && heredoc is { } h)
            {
                int pos = i + 1; StringBuilder body = new(); bool closed = false;
                while (pos <= cmd.Length)
                {
                    int eol = cmd.IndexOf('\n', pos); if (eol < 0) eol = cmd.Length;
                    string line = cmd[pos..eol];
                    if ((h.Dash ? line.Trim('\t') : line).TrimEnd('\r') == h.Word) { pos = eol; closed = true; break; }
                    body.AppendLine(line); pos = eol + 1;
                }
                if (!closed) return null;
                // An unquoted delimiter lets the body run $(…) and backticks.
                if (!h.Quoted) foreach (string sub in SubstitutionsIn(body.ToString())) inner.Add(sub);
                heredoc = null; segments.Add(cur.ToString()); cur.Clear(); i = pos; continue;
            }
            if (c is ';' or '\n' or '(' or ')') { segments.Add(cur.ToString()); cur.Clear(); continue; }
            if (c == '|') { if (next == '|') i++; segments.Add(cur.ToString()); cur.Clear(); continue; }
            if (c == '&')
            {
                if (next == '&') { i++; segments.Add(cur.ToString()); cur.Clear(); continue; }
                if (prev == '>' || next == '>') { cur.Append(c); continue; }   // part of a redirect (2>&1, &>)
                segments.Add(cur.ToString()); cur.Clear(); continue;          // a bare & runs the next command too
            }
            cur.Append(c);
        }
        if (quote is not null || heredoc is not null) return null;
        segments.Add(cur.ToString());
        return segments.Select(StripKeywords).Where(x => x.Length > 0).ToList();
    }

    private static int MatchingParen(string s, int open)
    {
        int depth = 0; char? q = null;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (q is not null) { if (c == q) q = null; continue; }
            if (c is '\'' or '"') { q = c; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SubstitutionsIn(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '(') { int e = MatchingParen(text, i + 1); if (e > 0) { yield return text[(i + 2)..e]; i = e; } }
            else if (text[i] == '`') { int e = text.IndexOf('`', i + 1); if (e > 0) { yield return text[(i + 1)..e]; i = e; } }
        }
    }

    // Shell keywords aren't commands: `do X`, `then X`, `if X`, `while X` run X; `for`/`case` lines, `done`, `fi` and braces
    // run nothing. Stripped so a loop is judged by what is in it, and `do rm -rf /` can't pass as the keyword `do`.
    private static readonly HashSet<string> LeadingKeywords = new(StringComparer.Ordinal) { "do", "then", "else", "if", "elif", "while", "until", "{", "}", "!" };
    private static readonly HashSet<string> NoCommandKeywords = new(StringComparer.Ordinal) { "for", "select", "case", "done", "fi", "esac", "in", ";;" };
    internal static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
        { "do", "then", "else", "if", "elif", "while", "until", "{", "}", "!", "for", "select", "case", "done", "fi", "esac", "in", "function" };

    private static string StripKeywords(string seg)
    {
        string t = seg.Trim();
        while (t.Length > 0)
        {
            int sp = t.IndexOfAny(new[] { ' ', '\t' });
            string word = sp < 0 ? t : t[..sp];
            if (NoCommandKeywords.Contains(word)) return "";
            if (!LeadingKeywords.Contains(word)) break;
            t = sp < 0 ? "" : t[sp..].TrimStart();
        }
        return t;
    }

    // Whitespace tokens with quoted runs kept whole and leading VAR=val assignments dropped.
    private static List<string> Tokenize(string seg)
    {
        List<string> tokens = new();
        StringBuilder cur = new();
        char? quote = null;
        foreach (char c in seg)
        {
            if (quote is not null) { if (c == quote) quote = null; else cur.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        while (tokens.Count > 0 && Regex.IsMatch(tokens[0], "^[A-Za-z_][A-Za-z0-9_]*=")) tokens.RemoveAt(0);
        return tokens;
    }

    // A program in a standard bin directory is named by its basename (/usr/bin/grep is grep); anything else exactly as
    // written, so approving ./build.sh never approves another file that happens to share its name.
    private static string ProgramName(string token) =>
        !token.Contains('/') ? token : TrustedBinDirs.Any(d => token.StartsWith(d, StringComparison.Ordinal)) ? token.Split('/')[^1] : token;

    private static (string? Sub, List<string> Args, bool RunsConfig) ParseGit(List<string> tokens)
    {
        int i = 1;
        bool runsConfig = false;
        while (i < tokens.Count && tokens[i].StartsWith('-'))
        {
            string t = tokens[i];
            // -c, --config-env and --exec-path can inject config or point git at other programs.
            if (t == "-c" || t.StartsWith("--config-env", StringComparison.Ordinal) || t.StartsWith("--exec-path", StringComparison.Ordinal)) runsConfig = true;
            i += GitValueOptions.Contains(t) ? 2 : 1;
        }
        return (i < tokens.Count ? tokens[i] : null, tokens.Skip(i + 1).ToList(), runsConfig);
    }

    // ── Danger and write rules ────────────────────────────────────────────────────────────

    private static bool HasFlag(List<string> args, params string[] flags) => args.Any(flags.Contains);
    // Short-flag clusters: -fu contains f and u. Only single-dash tokens.
    private static bool HasShort(List<string> args, string letters) =>
        args.Any(a => Regex.IsMatch(a, "^-[A-Za-z]+$") && letters.Any(l => a[1..].Contains(l)));

    private static string? GitDanger(string sub, List<string> args) => sub switch
    {
        "reset" => HasFlag(args, "--hard", "--merge") ? "git reset --hard throws away uncommitted work" : null,
        "push" => HasFlag(args, "--force", "--force-with-lease", "--force-if-includes", "--delete", "--mirror", "--prune") || HasShort(args, "fd")
                  || args.Any(a => a.StartsWith("--force-with-lease=", StringComparison.Ordinal) || a.StartsWith('+') || a.StartsWith(':'))
                  ? "a forced or deleting push rewrites or removes remote history" : null,
        "clean" => "git clean permanently deletes untracked files",
        "checkout" => HasFlag(args, "--", ".", "-f", "--force", "--ours", "--theirs", "-p", "--patch") ? "this checkout discards changes to files" : null,
        "restore" => HasFlag(args, "--staged", "-S") && !HasFlag(args, "--worktree", "-W") ? null : "git restore overwrites working-tree files",
        "branch" => HasFlag(args, "-D", "-f", "--force", "-M") || HasShort(args, "DfM") ? "this force-deletes or force-moves a branch" : null,
        "stash" => HasFlag(args, "drop", "clear") ? "this deletes stashed work" : null,
        "rebase" => "git rebase rewrites commit history",
        "commit" => HasFlag(args, "--amend") ? "git commit --amend rewrites the last commit" : null,
        "tag" => HasFlag(args, "-d", "--delete", "-f", "--force") ? "this deletes or moves a tag" : null,
        "reflog" => HasFlag(args, "expire", "delete") ? "this erases the reflog safety net" : null,
        "gc" or "prune" or "filter-branch" or "filter-repo" or "update-ref" or "replace" => "this rewrites or permanently removes git objects",
        _ => null,
    };

    private static string? GeneralDanger(string prog, List<string> args)
    {
        if (DangerousPrograms.TryGetValue(prog, out string? why)) return why;
        if (prog.StartsWith("mkfs", StringComparison.Ordinal)) return "formats a disk";
        if (prog == "rm" && (HasFlag(args, "--recursive", "--force") || HasShort(args, "rRf"))) return "rm -r / -f deletes files without recovery";
        if (prog is "chmod" or "chown" && (HasFlag(args, "--recursive") || HasShort(args, "R"))) return $"recursive {prog} changes many files at once";
        if (prog == "find" && HasFlag(args, "-delete", "-exec", "-execdir", "-ok", "-okdir")) return "find can delete files or run commands on each match";
        if (prog == "xargs") return "xargs runs another command on its input";
        return null;
    }

    // The write forms of read-only programs, and output redirected into a file (2>&1 and >/dev/null are fine).
    private static bool WritesFiles(string prog, List<string> args, string seg)
    {
        if (RedirectsToFile(seg)) return true;
        return prog switch
        {
            // -i / -i.bak / -ni edit in place; a script's w and e commands write files and run commands.
            "sed" => args.Any(a => a.StartsWith("--in-place", StringComparison.Ordinal) || Regex.IsMatch(a, "^-[A-Za-z]*i"))
                     || args.Any(a => !a.StartsWith('-') && Regex.IsMatch(a, @"(^|[;}\s/])[wWe](\s|$)")),
            "sort" => HasFlag(args, "-o") || args.Any(a => a.StartsWith("--output", StringComparison.Ordinal)),
            "find" => HasFlag(args, "-fprint", "-fprint0", "-fprintf", "-fls"),
            _ => false,
        };
    }

    private static bool RedirectsToFile(string seg)
    {
        char? quote = null;
        for (int i = 0; i < seg.Length; i++)
        {
            char c = seg[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c != '>') continue;
            int j = i + 1;
            if (j < seg.Length && seg[j] is '>' or '|') j++;
            if (j < seg.Length && seg[j] == '&') continue;                 // 2>&1, >&2
            string target = seg[j..].TrimStart();
            if (target.StartsWith("/dev/null", StringComparison.Ordinal)) { i = j; continue; }
            return true;
        }
        return false;
    }

    // ── Analysis ─────────────────────────────────────────────────────────────────────────

    // Programs that run another command given in their arguments: the command that runs is what gets judged.
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal) { "timeout", "env", "nohup", "time", "nice", "command", "stdbuf", "xargs" };

    // Options of each wrapper that take a value in the next token.
    private static readonly Dictionary<string, HashSet<string>> WrapperValueOptions = new(StringComparer.Ordinal)
    {
        ["timeout"] = new(StringComparer.Ordinal) { "-s", "--signal", "-k", "--kill-after" },
        ["env"] = new(StringComparer.Ordinal) { "-u", "--unset", "-C", "--chdir", "-S" },
        ["nice"] = new(StringComparer.Ordinal) { "-n", "--adjustment" },
        ["xargs"] = new(StringComparer.Ordinal) { "-I", "-n", "-P", "-d", "-s", "-L", "-E", "-a" },
        ["stdbuf"] = new(StringComparer.Ordinal),
    };

    // The command a wrapper runs, or null when the tokens aren't a wrapper (or it wraps nothing).
    private static List<string>? Unwrap(string prog, List<string> tokens)
    {
        if (!Wrappers.Contains(prog)) return null;
        int i = 1;
        WrapperValueOptions.TryGetValue(prog, out HashSet<string>? valueOpts);
        while (i < tokens.Count)
        {
            string t = tokens[i];
            if (prog == "env" && Regex.IsMatch(t, "^[A-Za-z_][A-Za-z0-9_]*=")) { i++; continue; }
            if (t == "--") { i++; break; }
            if (!t.StartsWith('-') || t == "-") break;
            i += valueOpts is not null && valueOpts.Contains(t) ? 2 : 1;
        }
        if (prog == "timeout" && i < tokens.Count && Regex.IsMatch(tokens[i], @"^\d")) i++;   // the duration
        if (i >= tokens.Count) return prog == "xargs" ? new List<string> { "echo" } : null;   // xargs with no command runs echo
        return tokens.Skip(i).ToList();
    }

    private static CommandSegment AnalyzeSegment(string seg) => AnalyzeTokens(Tokenize(seg), seg, 0);

    private static CommandSegment AnalyzeTokens(List<string> tokens, string seg, int depth)
    {
        if (tokens.Count == 0) return new(seg, null, CommandTier.Opaque, "empty command");
        string prog = ProgramName(tokens[0]);
        // A command held in a variable, or produced by a substitution, can be anything.
        if (prog.Contains('$') || prog.Contains('`')) return new(seg, null, CommandTier.Opaque, "the command to run is built from a variable or substitution");
        if (Unwrap(prog, tokens) is { } innerTokens)
        {
            if (depth >= 4) return new(seg, null, CommandTier.Opaque, "commands wrapped too deeply to check");
            CommandSegment inner = AnalyzeTokens(innerTokens, seg, depth + 1);
            // A redirect to a file on the wrapper's own line still writes.
            return inner.Tier == CommandTier.ReadOnly && RedirectsToFile(seg) ? inner with { Tier = CommandTier.Allowable } : inner;
        }
        List<string> args = tokens.Skip(1).ToList();

        if (prog == "git")
        {
            (string? sub, List<string> gitArgs, bool runsConfig) = ParseGit(tokens);
            if (sub is null) return new(seg, null, CommandTier.Opaque, "git without a subcommand");
            if (runsConfig) return new(seg, null, CommandTier.Opaque, "git is being given inline config or another exec path");
            if (GitDanger(sub, gitArgs) is { } gitDanger) return new(seg, null, CommandTier.Dangerous, gitDanger);
            string key = $"git {sub}";
            return new(seg, key, ReadOnlyGit.Contains(sub) && !RedirectsToFile(seg) ? CommandTier.ReadOnly : CommandTier.Allowable);
        }

        if (GeneralDanger(prog, args) is { } danger) return new(seg, null, CommandTier.Dangerous, danger);

        if (Multiplexers.Contains(prog))
        {
            string? sub = args.FirstOrDefault();
            if (sub is null || sub.StartsWith('-')) return new(seg, null, CommandTier.Opaque, $"{prog} without a subcommand");
            return new(seg, $"{prog} {sub}", CommandTier.Allowable);
        }

        bool readOnly = ReadOnlyPrograms.Contains(prog) && !WritesFiles(prog, args, seg);
        return new(seg, prog, readOnly ? CommandTier.ReadOnly : CommandTier.Allowable);
    }

    /// <summary>Every command the line runs, analysed, including the ones inside $(…), backticks and loops. Quotes or
    /// brackets that don't balance → one opaque segment.</summary>
    public static List<CommandSegment> Analyze(string command) => Analyze(command, 0);

    private static List<CommandSegment> Analyze(string command, int depth)
    {
        List<string> inner = new();
        List<string>? segs = depth > 4 ? null : Segments(command, out inner);
        if (segs is null)
            return new() { new(command.Trim(), null, CommandTier.Opaque, "it can't be parsed safely (unbalanced quotes or brackets)") };
        List<CommandSegment> result = segs.Select(AnalyzeSegment).ToList();
        foreach (string sub in inner) result.AddRange(Analyze(sub, depth + 1));
        return result;
    }

    /// <summary>True if this command runs without a prompt: read-only, or allowable with its key on the allowlist.</summary>
    public static bool Approved(CommandSegment s, IReadOnlyCollection<string> allowlist) =>
        s.Tier == CommandTier.ReadOnly || (s.Tier == CommandTier.Allowable && s.Key is not null && allowlist.Contains(s.Key));

    /// <summary>The commands the owner still has to decide on, one per distinct command (so "git add a &amp;&amp; git add b"
    /// asks once). Empty when the whole line can run as is.</summary>
    public static List<CommandSegment> NeedingDecision(string command, IReadOnlyCollection<string> allowlist)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        return Analyze(command).Where(s => !Approved(s, allowlist) && seen.Add(s.Key ?? s.Text)).ToList();
    }

    /// <summary>A key that may be stored on the allowlist: not blank, not a bare multiplexer ("git" would approve every
    /// subcommand, destructive ones included), not a program that's always dangerous.</summary>
    public static bool StorableKey(string key) =>
        key.Length > 0 && !Multiplexers.Contains(key) && !DangerousPrograms.ContainsKey(key) && !key.StartsWith("mkfs", StringComparison.Ordinal)
        && !ReservedWords.Contains(key) && !Wrappers.Contains(key);
}
