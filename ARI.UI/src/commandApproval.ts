// run_command authorisation — pure logic, no UI and no I/O, so it can be unit tested (tests/commandApproval.test.ts).
//
// A command line is split into segments (pipes, &&, ;). Each segment is analysed into a KEY ("git commit",
// "dotnet build", "grep") and a TIER:
//   • allowable — can be approved "every time": the key is stored in the (global) allowlist.
//   • dangerous — destructive forms (git reset --hard, git push --force, rm -rf, sudo, …). Always allowed to run
//                 when the user says so, but NEVER stored: they ask again every time.
//   • opaque    — can't be decomposed safely (command substitution, bare `git`, an unparseable form). Ask every time.
// A command runs without a prompt only when every segment is approved. The user decides per new command.

export type SegmentTier = "allowable" | "dangerous" | "opaque"

export interface SegmentInfo {
    text:    string           // the segment as written
    key:     string | null    // allowlist key; null for dangerous/opaque (never stored)
    tier:    SegmentTier
    reason?: string           // why it is dangerous/opaque — shown in the prompt
}

// Programs whose first sub-word is the real command ("dotnet build", not all of "dotnet").
export const MULTIPLEXERS = new Set(["git", "dotnet", "npm", "yarn", "pnpm", "bun", "deno", "docker", "cargo", "go", "kubectl", "pip", "pip3", "brew", "make"])

// Run with no prompt at all: unambiguous read-only commands. Everything else is approved per command.
// (`git branch`, `stash`, `tag`, `remote`, `fetch`, `reflog` and `grep` have write forms, so they are not here.)
const AUTO_ALLOWED = new Set([
    "cd", "pwd",
    "git status", "git log", "git diff", "git show", "git blame", "git rev-parse", "git ls-files", "git ls-tree",
    "git describe", "git shortlog", "git cat-file", "git name-rev", "git for-each-ref", "git count-objects",
])

// Programs that are dangerous however they are called.
const DANGEROUS_PROGRAMS = new Map<string, string>([
    ["sudo", "runs with elevated privileges"], ["su", "switches user"], ["doas", "runs with elevated privileges"],
    ["dd", "writes raw data to disks or files"], ["fdisk", "edits disk partitions"], ["diskutil", "manages disks"],
    ["shutdown", "shuts the machine down"], ["reboot", "reboots the machine"], ["halt", "halts the machine"],
    ["sh", "runs arbitrary shell code"], ["bash", "runs arbitrary shell code"], ["zsh", "runs arbitrary shell code"],
    ["fish", "runs arbitrary shell code"], ["eval", "runs arbitrary shell code"], ["exec", "replaces the shell with another program"],
])

const TRUSTED_BIN_DIRS = ["/usr/bin/", "/bin/", "/usr/local/bin/", "/opt/homebrew/bin/"]

// ── Parsing ──────────────────────────────────────────────────────────────────────────────

// Splits a command line into pipe/sequence segments, respecting quotes. Returns null when it contains command
// substitution ($(...), backticks, <(...)) or unbalanced quotes — those can hide arbitrary commands.
// Quote-aware so a "|" inside grep -E "(a|b)" isn't a pipe and the "&" in 2>&1 isn't a sequence operator.
export function commandSegments(command: string): string[] | null {
    const cmd = command.trim()
    if (!cmd) return null
    if (/\$\(|`|<\(/.test(cmd)) return null

    const segments: string[] = []
    let cur = "", quote: string | null = null
    for (let i = 0; i < cmd.length; i++) {
        const c = cmd[i], next = cmd[i + 1], prev = cmd[i - 1]
        if (quote) { cur += c; if (c === quote) quote = null; continue }
        if (c === "'" || c === '"') { quote = c; cur += c; continue }
        if (c === ";" || c === "\n") { segments.push(cur); cur = ""; continue }
        if (c === "|") { if (next === "|") i++; segments.push(cur); cur = ""; continue }
        if (c === "&") {
            if (next === "&") { i++; segments.push(cur); cur = ""; continue }
            if (prev === ">" || next === ">") { cur += c; continue }  // part of a redirect (2>&1, &>)
            segments.push(cur); cur = ""; continue                    // bare & — background/sequence
        }
        cur += c
    }
    if (quote) return null
    segments.push(cur)
    return segments.map(s => s.trim()).filter(Boolean)
}

// Whitespace tokens with quoted runs kept whole and leading VAR=val assignments dropped.
function tokenize(seg: string): string[] {
    const tokens: string[] = []
    let cur = "", quote: string | null = null
    for (const c of seg) {
        if (quote) { if (c === quote) quote = null; else cur += c; continue }
        if (c === "'" || c === '"') { quote = c; continue }
        if (/\s/.test(c)) { if (cur) { tokens.push(cur); cur = "" } continue }
        cur += c
    }
    if (cur) tokens.push(cur)
    while (tokens.length && /^[A-Za-z_][A-Za-z0-9_]*=/.test(tokens[0])) tokens.shift()
    return tokens
}

// How a program is named in the allowlist: its basename when it lives in a standard bin directory
// (/usr/bin/grep → grep), otherwise exactly as written (./build.sh stays ./build.sh, so approving one
// script never approves a different file that happens to share its basename).
function programName(token: string): string {
    if (!token.includes("/")) return token
    return TRUSTED_BIN_DIRS.some(d => token.startsWith(d)) ? token.split("/").pop() || token : token
}

// git global options that take a separate value (`git -C dir log`); the rest of the leading -flags don't.
const GIT_VALUE_OPTIONS = new Set(["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--super-prefix", "--config-env"])

interface GitCall { sub: string | null; args: string[]; runsConfig: boolean }

function parseGit(tokens: string[]): GitCall {
    let i = 1, runsConfig = false
    while (i < tokens.length && tokens[i].startsWith("-")) {
        const t = tokens[i]
        // -c / --config-env / --exec-path can inject config or point git at other programs
        if (t === "-c" || t.startsWith("--config-env") || t.startsWith("--exec-path")) runsConfig = true
        i += GIT_VALUE_OPTIONS.has(t) ? 2 : 1
    }
    return { sub: tokens[i] ?? null, args: tokens.slice(i + 1), runsConfig }
}

// ── Danger rules ─────────────────────────────────────────────────────────────────────────

const hasFlag = (args: string[], ...flags: string[]) => args.some(a => flags.includes(a))
// Short-flag clusters: -fu contains f and u. Only looks at single-dash tokens.
const hasShort = (args: string[], letters: string) =>
    args.some(a => /^-[A-Za-z]+$/.test(a) && [...letters].some(l => a.slice(1).includes(l)))

function gitDanger(sub: string, args: string[]): string | null {
    switch (sub) {
        case "reset":
            return hasFlag(args, "--hard", "--merge") ? "git reset --hard throws away uncommitted work" : null
        case "push":
            if (hasFlag(args, "--force", "--force-with-lease", "--force-if-includes", "--delete", "--mirror", "--prune") || hasShort(args, "fd")
                || args.some(a => a.startsWith("--force-with-lease=") || a.startsWith("+") || a.startsWith(":")))
                return "a forced or deleting push rewrites or removes remote history"
            return null
        case "clean":
            return "git clean permanently deletes untracked files"
        case "checkout":
            return hasFlag(args, "--", ".", "-f", "--force", "--ours", "--theirs", "-p", "--patch")
                ? "this checkout discards changes to files" : null
        case "restore":
            return hasFlag(args, "--staged", "-S") && !hasFlag(args, "--worktree", "-W")
                ? null : "git restore overwrites working-tree files"
        case "branch":
            return hasFlag(args, "-D", "-f", "--force", "-M") || hasShort(args, "DfM")
                ? "this force-deletes or force-moves a branch" : null
        case "stash":
            return hasFlag(args, "drop", "clear") ? "this deletes stashed work" : null
        case "rebase":
            return "git rebase rewrites commit history"
        case "commit":
            return hasFlag(args, "--amend") ? "git commit --amend rewrites the last commit" : null
        case "tag":
            return hasFlag(args, "-d", "--delete", "-f", "--force") ? "this deletes or moves a tag" : null
        case "reflog":
            return hasFlag(args, "expire", "delete") ? "this erases the reflog safety net" : null
        case "gc": case "prune": case "filter-branch": case "filter-repo": case "update-ref": case "replace":
            return "this rewrites or permanently removes git objects"
        default:
            return null
    }
}

function generalDanger(prog: string, args: string[]): string | null {
    const byProgram = DANGEROUS_PROGRAMS.get(prog)
    if (byProgram) return byProgram
    if (prog.startsWith("mkfs")) return "formats a disk"
    if (prog === "rm" && (hasFlag(args, "--recursive", "--force") || hasShort(args, "rRf"))) return "rm -r / -f deletes files without recovery"
    if ((prog === "chmod" || prog === "chown") && (hasFlag(args, "--recursive") || hasShort(args, "R"))) return `recursive ${prog} changes many files at once`
    if (prog === "find" && hasFlag(args, "-delete", "-exec", "-execdir", "-ok")) return "find can delete files or run commands on each match"
    if (prog === "xargs") return "xargs runs another command on its input"
    return null
}

// ── Analysis ─────────────────────────────────────────────────────────────────────────────

function analyzeSegment(seg: string): SegmentInfo {
    const tokens = tokenize(seg)
    if (tokens.length === 0) return { text: seg, key: null, tier: "opaque", reason: "empty command" }
    const prog = programName(tokens[0])

    if (prog === "git") {
        const g = parseGit(tokens)
        if (!g.sub) return { text: seg, key: null, tier: "opaque", reason: "git without a subcommand" }
        if (g.runsConfig) return { text: seg, key: null, tier: "opaque", reason: "git is being given inline config or another exec path" }
        const danger = gitDanger(g.sub, g.args)
        if (danger) return { text: seg, key: null, tier: "dangerous", reason: danger }
        return { text: seg, key: `git ${g.sub}`, tier: "allowable" }
    }

    const danger = generalDanger(prog, tokens.slice(1))
    if (danger) return { text: seg, key: null, tier: "dangerous", reason: danger }

    if (MULTIPLEXERS.has(prog)) {
        const sub = tokens[1]
        if (!sub || sub.startsWith("-")) return { text: seg, key: null, tier: "opaque", reason: `${prog} without a subcommand` }
        return { text: seg, key: `${prog} ${sub}`, tier: "allowable" }
    }
    return { text: seg, key: prog, tier: "allowable" }
}

/** Splits a command into analysed segments. Command substitution / unbalanced quotes → one opaque segment. */
export function analyzeCommand(command: string): SegmentInfo[] {
    const segs = commandSegments(command)
    if (!segs || segs.length === 0)
        return [{ text: command.trim(), key: null, tier: "opaque", reason: "it contains command substitution or can't be parsed safely" }]
    return segs.map(analyzeSegment)
}

/** True if this segment runs without a prompt: a built-in read-only command, or an approved key. Dangerous and
 *  opaque segments are never approved, however the allowlist looks. */
export function segmentApproved(info: SegmentInfo, allowlist: readonly string[]): boolean {
    if (info.tier !== "allowable" || !info.key) return false
    return AUTO_ALLOWED.has(info.key) || allowlist.includes(info.key)
}

/** The segments the user still has to decide on, one entry per distinct command (so `git add . && git add -A`
 *  asks once). Empty when the whole command can run as is. */
export function segmentsNeedingDecision(command: string, allowlist: readonly string[]): SegmentInfo[] {
    const seen = new Set<string>()
    const out: SegmentInfo[] = []
    for (const info of analyzeCommand(command)) {
        if (segmentApproved(info, allowlist)) continue
        const id = info.key ?? info.text
        if (seen.has(id)) continue
        seen.add(id)
        out.push(info)
    }
    return out
}

export const commandIsApproved = (command: string, allowlist: readonly string[]) =>
    segmentsNeedingDecision(command, allowlist).length === 0

/** Cleans a stored allowlist: drops blanks and duplicates, and bare multiplexer entries ("git", "dotnet") that an
 *  older version could store — those would approve every subcommand, destructive ones included. */
export function sanitizeAllowlist(list: unknown): string[] {
    if (!Array.isArray(list)) return []
    const out: string[] = []
    for (const raw of list) {
        if (typeof raw !== "string") continue
        const key = raw.trim()
        if (!key || out.includes(key)) continue
        if (MULTIPLEXERS.has(key)) continue
        if (DANGEROUS_PROGRAMS.has(key) || key.startsWith("mkfs")) continue
        out.push(key)
    }
    return out
}

// ── The decision loop ────────────────────────────────────────────────────────────────────

export type CommandDecision = "deny" | "allow" | "whitelist"

export interface DecideDeps {
    /** Shows one prompt for one segment and resolves with the user's choice. */
    ask:     (segment: SegmentInfo, command: string) => Promise<CommandDecision>
    /** Saves the (global) allowlist; resolves false if it couldn't be saved. */
    persist: (list: string[]) => Promise<boolean>
}

export interface Decision {
    allowed:   boolean      // the command may run
    allowlist: string[]     // the allowlist after this decision (new entries added)
    unsaved:   string[]     // keys approved "every time" that could not be saved — good for this session only
}

/** Decides one command: every segment that isn't already approved gets its own prompt, in order. One denial stops
 *  the whole command. "Every time" is stored for allowable segments only — dangerous and opaque segments can only
 *  ever be allowed this once, whatever the UI sends back. */
export async function decideCommand(command: string, allowlist: readonly string[], deps: DecideDeps): Promise<Decision> {
    let current = [...allowlist]
    const unsaved: string[] = []
    for (const segment of segmentsNeedingDecision(command, current)) {
        const decision = await deps.ask(segment, command)
        if (decision === "deny") return { allowed: false, allowlist: current, unsaved }
        if (decision === "whitelist" && segment.tier === "allowable" && segment.key && !current.includes(segment.key)) {
            current = [...current, segment.key]
            if (!await deps.persist(current)) unsaved.push(segment.key)
        }
    }
    return { allowed: true, allowlist: current, unsaved }
}
