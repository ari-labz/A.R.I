import { describe, expect, test } from "bun:test"
import {
    analyzeCommand, commandIsApproved, commandSegments, sanitizeAllowlist, segmentsNeedingDecision,
} from "../src/commandApproval"

const keysOf = (c: string) => analyzeCommand(c).map(s => s.key)
const tierOf = (c: string) => analyzeCommand(c).map(s => s.tier)

describe("segments", () => {
    test("splits on pipes, &&, ; but not inside quotes or 2>&1", () => {
        expect(commandSegments("a && b | c; d")).toEqual(["a", "b", "c", "d"])
        expect(commandSegments('grep -E "(a|b)" f')).toEqual(['grep -E "(a|b)" f'])
        expect(commandSegments("dotnet build 2>&1")).toEqual(["dotnet build 2>&1"])
    })
    test("command substitution / unbalanced quotes are undecomposable", () => {
        expect(commandSegments("echo $(rm -rf /)")).toBeNull()
        expect(commandSegments("echo `x`")).toBeNull()
        expect(commandSegments('echo "oops')).toBeNull()
        expect(tierOf("echo $(whoami)")).toEqual(["opaque"])
    })
})

describe("keys", () => {
    test("plain programs and multiplexers", () => {
        expect(keysOf("grep -rn foo .")).toEqual(["grep"])
        expect(keysOf("dotnet build -c Release")).toEqual(["dotnet build"])
        expect(keysOf("npm run build")).toEqual(["npm run"])
        expect(keysOf("/usr/bin/grep x")).toEqual(["grep"])
        expect(keysOf("./build.sh --fast")).toEqual(["./build.sh"])
    })
    test("git: the key is the subcommand, whatever global options come first", () => {
        expect(keysOf("git commit -m 'a message'")).toEqual(["git commit"])
        expect(keysOf("git --no-pager log -5")).toEqual(["git log"])
        expect(keysOf("git -C ../other status")).toEqual(["git status"])
        expect(keysOf("git --git-dir=x.git --work-tree=. add -A")).toEqual(["git add"])
    })
    test("bare multiplexer or bare git is opaque, never keyed", () => {
        expect(tierOf("git")).toEqual(["opaque"])
        expect(tierOf("git --version")).toEqual(["opaque"])
        expect(tierOf("dotnet --info")).toEqual(["opaque"])
    })
    test("git -c (inline config) is opaque", () => {
        expect(tierOf("git -c core.sshCommand=evil push")).toEqual(["opaque"])
    })
})

describe("dangerous commands are allowed to run but never stored", () => {
    const dangerous = [
        "git reset --hard HEAD~1", "git reset --hard", "git push --force", "git push -f origin main",
        "git push --force-with-lease", "git push -fu origin x", "git push origin +main", "git push origin :old-branch",
        "git push --delete origin x", "git clean -fd", "git checkout -- .", "git checkout .", "git restore src/a.cs",
        "git branch -D old", "git stash drop", "git stash clear", "git rebase main", "git commit --amend",
        "git tag -d v1", "git reflog expire --all", "git gc --prune=now", "git filter-branch --all",
        "rm -rf build", "rm -r dir", "rm -f x", "sudo ls", "dd if=/dev/zero of=x", "find . -name x -delete",
        "chmod -R 777 .", "bash -c 'x'", "xargs rm",
    ]
    test.each(dangerous)("%s is dangerous", c => {
        expect(tierOf(c).includes("dangerous")).toBe(true)
        expect(keysOf(c)).toEqual(keysOf(c).map(() => null))
    })
    test("an allowlist can't approve them, even with matching-looking entries", () => {
        const list = ["git reset", "git push", "git clean", "git", "rm", "sudo"]
        expect(commandIsApproved("git reset --hard", list)).toBe(false)
        expect(commandIsApproved("git push --force", list)).toBe(false)
        expect(commandIsApproved("rm -rf x", list)).toBe(false)
        expect(commandIsApproved("git clean -fd", list)).toBe(false)
    })
    test("but the safe forms of the same commands are fine", () => {
        expect(tierOf("git reset --soft HEAD~1")).toEqual(["allowable"])
        expect(tierOf("git reset HEAD file")).toEqual(["allowable"])
        expect(tierOf("git push origin main")).toEqual(["allowable"])
        expect(tierOf("git push -u origin feature")).toEqual(["allowable"])
        expect(tierOf("git checkout main")).toEqual(["allowable"])
        expect(tierOf("git checkout -b new")).toEqual(["allowable"])
        expect(tierOf("git restore --staged f")).toEqual(["allowable"])
        expect(tierOf("git branch -d merged")).toEqual(["allowable"])
        expect(tierOf("git stash")).toEqual(["allowable"])
        expect(tierOf("git commit -m x")).toEqual(["allowable"])
        expect(tierOf("rm one-file.txt")).toEqual(["allowable"])
    })
})

describe("approval", () => {
    test("an approved key auto-runs later commands of the same kind, whatever the arguments", () => {
        const list = ["git commit"]
        expect(commandIsApproved("git commit -m 'first'", list)).toBe(true)
        expect(commandIsApproved("git commit -m 'second' --no-verify", list)).toBe(true)
        expect(commandIsApproved("git --no-pager commit -a", list)).toBe(true)
        expect(commandIsApproved("git add .", list)).toBe(false)           // a different command asks on its own
    })
    test("approving one subcommand never approves the rest of the program", () => {
        expect(commandIsApproved("git push origin main", ["git commit"])).toBe(false)
        expect(commandIsApproved("dotnet test", ["dotnet build"])).toBe(false)
    })
    test("read-only git and cd need no prompt at all", () => {
        expect(commandIsApproved("git status", [])).toBe(true)
        expect(commandIsApproved("git --no-pager log -n 5 --oneline", [])).toBe(true)
        expect(commandIsApproved("git diff HEAD~1", [])).toBe(true)
        expect(commandIsApproved("cd Kraken && git status", [])).toBe(true)
    })
    test("a chain asks only about what is new, once per distinct command", () => {
        const need = segmentsNeedingDecision("git add . && git commit -m x && git push", ["git add"])
        expect(need.map(n => n.key)).toEqual(["git commit", "git push"])
        const dupes = segmentsNeedingDecision("git add a && git add b", [])
        expect(dupes.map(n => n.key)).toEqual(["git add"])
    })
    test("a dangerous segment in a chain always asks, approved neighbours don't", () => {
        const need = segmentsNeedingDecision("git add . && git push --force", ["git add", "git push"])
        expect(need).toHaveLength(1)
        expect(need[0].tier).toBe("dangerous")
    })
    test("unknown / opaque always asks", () => {
        expect(commandIsApproved("echo $(date)", ["echo"])).toBe(false)
        expect(commandIsApproved("git", ["git"])).toBe(false)
    })
})

describe("sanitizeAllowlist", () => {
    test("drops blanks, duplicates, non-strings, and entries that would approve a whole program", () => {
        expect(sanitizeAllowlist(["git", "git log", " git log ", "", "dotnet", "dotnet build", 5, "grep", "sudo", "mkfs.ext4"]))
            .toEqual(["git log", "dotnet build", "grep"])
        expect(sanitizeAllowlist(null)).toEqual([])
    })
})

import { decideCommand, type CommandDecision, type SegmentInfo } from "../src/commandApproval"

// A scripted user: answers each prompt in order and records what it was asked.
function user(...answers: CommandDecision[]) {
    const asked: SegmentInfo[] = []
    const saved: string[][] = []
    return {
        asked, saved,
        deps: {
            ask:     async (seg: SegmentInfo) => { asked.push(seg); return answers.shift() ?? "deny" },
            persist: async (list: string[]) => { saved.push(list); return true },
        },
    }
}

describe("decideCommand — the per-command prompt flow", () => {
    test("a new command asks once; 'every time' stores it, and the next one doesn't ask", async () => {
        const u = user("whitelist")
        const first = await decideCommand("git commit -m 'one'", [], u.deps)
        expect(first.allowed).toBe(true)
        expect(first.allowlist).toEqual(["git commit"])
        expect(u.saved).toEqual([["git commit"]])
        const u2 = user()
        const second = await decideCommand("git commit -m 'two'", first.allowlist, u2.deps)
        expect(second.allowed).toBe(true)
        expect(u2.asked).toHaveLength(0)
    })
    test("'just this time' runs it but stores nothing, so it asks again next time", async () => {
        const u = user("allow")
        const first = await decideCommand("git add .", [], u.deps)
        expect(first.allowed).toBe(true)
        expect(first.allowlist).toEqual([])
        expect(u.saved).toHaveLength(0)
        const u2 = user("allow")
        await decideCommand("git add .", first.allowlist, u2.deps)
        expect(u2.asked).toHaveLength(1)
    })
    test("deny stops the command", async () => {
        const u = user("deny")
        const d = await decideCommand("git push origin main", [], u.deps)
        expect(d.allowed).toBe(false)
        expect(d.allowlist).toEqual([])
    })
    test("a chain prompts per new command, in order, and one denial stops the rest", async () => {
        const u = user("whitelist", "deny")
        const d = await decideCommand("git add . && git commit -m x && git push", [], u.deps)
        expect(u.asked.map(a => a.key)).toEqual(["git add", "git commit"])   // never reached git push
        expect(d.allowed).toBe(false)
        expect(d.allowlist).toEqual(["git add"])                             // the one approved before the denial still sticks
    })
    test("dangerous commands run when allowed but are NEVER stored, even if the UI answers 'whitelist'", async () => {
        const u = user("whitelist")
        const d = await decideCommand("git push --force origin main", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual([])
        expect(u.saved).toHaveLength(0)
        const again = user("allow")
        await decideCommand("git push --force origin main", d.allowlist, again.deps)
        expect(again.asked).toHaveLength(1)                                   // asks every single time
        expect(again.asked[0].tier).toBe("dangerous")
    })
    test("reset --hard: same", async () => {
        const u = user("whitelist")
        const d = await decideCommand("git reset --hard HEAD~1", ["git reset"], u.deps)
        expect(u.asked).toHaveLength(1)
        expect(d.allowlist).toEqual(["git reset"])
        expect(u.saved).toHaveLength(0)
    })
    test("opaque commands are never stored either", async () => {
        const u = user("whitelist")
        const d = await decideCommand("echo $(date)", [], u.deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual([])
    })
    test("a failed save is reported but the approval still holds for the session", async () => {
        const deps = { ask: async () => "whitelist" as CommandDecision, persist: async () => false }
        const d = await decideCommand("git commit -m x", [], deps)
        expect(d.allowed).toBe(true)
        expect(d.allowlist).toEqual(["git commit"])
        expect(d.unsaved).toEqual(["git commit"])
    })
    test("everything already approved: no prompt at all", async () => {
        const u = user()
        const d = await decideCommand("git status && git --no-pager diff && git commit -m z", ["git commit"], u.deps)
        expect(d.allowed).toBe(true)
        expect(u.asked).toHaveLength(0)
    })
})
