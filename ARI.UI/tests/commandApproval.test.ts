import { describe, expect, test } from "bun:test"
import { cleanAllowlist, decideCommand, type CommandDecision, type SegmentInfo } from "../src/commandApproval"

// The splitting and classifying rules are the server's (ARI.LLM CommandPolicy); these tests cover the prompt loop around
// them, with the server's answer scripted.

const seg = (key: string | null, tier: SegmentInfo["tier"] = "allowable", text = key ?? "x"): SegmentInfo => ({ text, key, tier })

// A scripted user and server: the server returns `needing`; the user answers each prompt in order.
function setup(needing: SegmentInfo[] | null, ...answers: CommandDecision[]) {
    const asked: SegmentInfo[] = []
    const saved: string[][] = []
    return {
        asked, saved,
        deps: {
            check:   async () => needing,
            ask:     async (s: SegmentInfo) => { asked.push(s); return answers.shift() ?? "deny" },
            persist: async (list: string[]) => { saved.push(list); return true },
        },
    }
}

describe("decideCommand — the per-command prompt flow", () => {
    test("nothing needing a decision (read-only or already allowed): no prompt", async () => {
        const u = setup([])
        const d = await decideCommand("grep x f | head", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(u.asked).toHaveLength(0)
    })
    test("'every time' stores the key", async () => {
        const u = setup([seg("git commit")], "whitelist")
        const d = await decideCommand("git commit -m 'one'", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual(["git commit"])
        expect(u.saved).toEqual([["git commit"]])
    })
    test("'just this time' runs it but stores nothing", async () => {
        const u = setup([seg("git add")], "allow")
        const d = await decideCommand("git add .", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual([])
        expect(u.saved).toHaveLength(0)
    })
    test("deny stops the command", async () => {
        const u = setup([seg("git push")], "deny")
        const d = await decideCommand("git push origin main", [], u.deps)
        expect(d.allowed).toBe(false)
    })
    test("a chain prompts per command, in order, and one denial stops the rest", async () => {
        const u = setup([seg("git add"), seg("git commit"), seg("git push")], "whitelist", "deny")
        const d = await decideCommand("git add . && git commit -m x && git push", [], u.deps)
        expect(u.asked.map(a => a.key)).toEqual(["git add", "git commit"])   // never reached git push
        expect(d.allowed).toBe(false)
        expect(d.allowlist).toEqual(["git add"])                             // the one approved before the denial sticks
    })
    test("dangerous and opaque commands are never stored, even if the UI answers 'whitelist'", async () => {
        const u = setup([seg(null, "dangerous", "git push --force"), seg(null, "opaque", "echo $(date)")], "whitelist", "whitelist")
        const d = await decideCommand("git push --force; echo $(date)", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual([])
        expect(u.saved).toHaveLength(0)
    })
    test("a key approved earlier this session (unsaved) isn't asked about again", async () => {
        const u = setup([seg("dotnet build")])
        const d = await decideCommand("dotnet build", ["dotnet build"], u.deps)
        expect(d.allowed).toBe(true)
        expect(u.asked).toHaveLength(0)
    })
    test("a failed save is reported but the approval still holds for the session", async () => {
        const deps = { check: async () => [seg("git commit")], ask: async () => "whitelist" as CommandDecision, persist: async () => false }
        const d = await decideCommand("git commit -m x", [], deps)
        expect(d.allowlist).toEqual(["git commit"])
        expect(d.unsaved).toEqual(["git commit"])
    })
    test("a line the server can't check is asked about as a whole, and never stored", async () => {
        const u = setup(null, "whitelist")
        const d = await decideCommand("make deploy", [], u.deps)
        expect(u.asked).toHaveLength(1)
        expect(u.asked[0].tier).toBe("opaque")
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual([])
    })
})

describe("cleanAllowlist", () => {
    test("trims, drops blanks, duplicates and non-strings", () => {
        expect(cleanAllowlist(["git log", " git log ", "", 5, "grep"])).toEqual(["git log", "grep"])
        expect(cleanAllowlist(null)).toEqual([])
    })
})
