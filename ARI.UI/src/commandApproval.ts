// run_command authorisation. The rules — how a line splits into commands, each one's key ("grep", "git commit"), what is
// read-only, dangerous or opaque — live on the server (ARI.LLM CommandPolicy), shared with the shell tool, so both paths
// behave the same. This asks the server which commands in a line still need a decision, then runs the prompt loop. No UI
// and no I/O of its own (both come in as deps), so it can be unit tested (tests/commandApproval.test.ts).

export type SegmentTier = "readonly" | "allowable" | "dangerous" | "opaque"

export interface SegmentInfo {
    text:    string           // the command as written
    key:     string | null    // allowlist key; null for dangerous/opaque (never stored)
    tier:    SegmentTier
    reason?: string           // why it is dangerous/opaque — shown in the prompt
}

export type CommandDecision = "deny" | "allow" | "whitelist"

/** Trims and de-duplicates a stored allowlist. The server drops keys that must never be stored when it saves. */
export function cleanAllowlist(list: unknown): string[] {
    if (!Array.isArray(list)) return []
    const out: string[] = []
    for (const raw of list) {
        if (typeof raw !== "string") continue
        const key = raw.trim()
        if (key && !out.includes(key)) out.push(key)
    }
    return out
}

export interface DecideDeps {
    /** The commands in the line that still need a decision (server's CommandPolicy); null when that can't be checked. */
    check:   (command: string) => Promise<SegmentInfo[] | null>
    /** Shows one prompt for one command and resolves with the user's choice. */
    ask:     (segment: SegmentInfo, command: string) => Promise<CommandDecision>
    /** Saves the (global) allowlist; resolves false if it couldn't be saved. */
    persist: (list: string[]) => Promise<boolean>
}

export interface Decision {
    allowed:   boolean      // the command may run
    allowlist: string[]     // the allowlist after this decision (new entries added)
    unsaved:   string[]     // keys approved "every time" that could not be saved — good for this session only
}

/** Decides one command line: every command that isn't already approved gets its own prompt, in order. One denial stops
 *  the whole line. "Every time" is stored for allowable commands only — dangerous and opaque ones can only ever be
 *  allowed this once, whatever the UI sends back. A line the server can't check is asked about as a whole. */
export async function decideCommand(command: string, allowlist: readonly string[], deps: DecideDeps): Promise<Decision> {
    let current = [...allowlist]
    const unsaved: string[] = []
    const needing = await deps.check(command)
        ?? [{ text: command.trim(), key: null, tier: "opaque" as const, reason: "ARI couldn't check this command" }]
    for (const segment of needing) {
        // A key approved earlier this session but not saved is still honoured.
        if (segment.tier === "allowable" && segment.key && current.includes(segment.key)) continue
        const decision = await deps.ask(segment, command)
        if (decision === "deny") return { allowed: false, allowlist: current, unsaved }
        if (decision === "whitelist" && segment.tier === "allowable" && segment.key && !current.includes(segment.key)) {
            current = [...current, segment.key]
            if (!await deps.persist(current)) unsaved.push(segment.key)
        }
    }
    return { allowed: true, allowlist: current, unsaved }
}
