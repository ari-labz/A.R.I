import { useState, useEffect, useCallback, useRef, type CSSProperties } from "react"
import Sidebar from "./components/Sidebar"
import Main from "./components/Main"
import ProjectsPage from "./components/ProjectsPage"
import {
    useThreads, createThread, closeThread, loadHistory, fetchThread, pollThreadWhileStreaming,
    openEventStream, cancelProcessing, useTypingHeartbeat,
    type ThreadItem, type ThreadEntry, type AppEvent, type Attachment, type Project,
} from "./hooks/useThreads"
import { usePipelines } from "./hooks/usePipelines"
import { startListening, type ListenerHandle } from "./hooks/useListener"
import { env } from "./env"
import { playResponseChime } from "./notify"
import "./styles/app.css"

export type AppMode = "idle" | "active"

// Phone or tablet in portrait — matches the CSS breakpoints that give the sidebar overlay-drawer
// behavior instead of a persistent column (app.css: max-width:640px, and the 641–1024px tablet-portrait
// block). Narrow-and-tall is what makes a permanently open sidebar cost too much width to justify.
function isNarrowPortrait(): boolean {
    if (typeof window === "undefined" || !window.matchMedia) return false
    return window.matchMedia("(max-width: 1024px) and (orientation: portrait)").matches
}

function buildSafetyDiff(newStr: string, startLine?: number, endLine?: number): string {
    const range = startLine != null
        ? ` (replacing lines ${startLine}${endLine != null && endLine !== startLine ? `–${endLine}` : ""})`
        : ""
    const added = newStr.split("\n").map(l => `+ ${l}`).join("\n")
    return `\`\`\`diff\n# proposed${range}\n${added}\n\`\`\``
}

export interface PendingAttachment {
    name:      string
    isImage:   boolean
    mimeType:  string | null
    content:   string | null
    uploading: boolean
}

const COMMANDS = [
    { cmd: "/code",          desc: "Switch this thread to Code mode" },
    { cmd: "/uncode",        desc: "Switch this thread back to Dialogue mode" },
    { cmd: "/engram on",     desc: "Enable engram memory extraction" },
    { cmd: "/engram off",    desc: "Disable engram memory extraction" },
    { cmd: "/engram sweep",  desc: "Run a manual engram sweep now" },
    { cmd: "/engram status", desc: "Show engram enabled/disabled state" },
    { cmd: "/refactor",      desc: "Incremental refactor — process dirty notes + 1-hop neighbours" },
    { cmd: "/refactor all",  desc: "Full graph refactor — scans every note (use for first run or rebuild)" },
    { cmd: "/purge notes",   desc: "Delete all notes from the brain" },
    { cmd: "/brain backup",  desc: "Export a backup of the brain" },
]

export { COMMANDS }

// ── run_command authorization ─────────────────────────────────────────────────
type CommandDecision = "deny" | "allow" | "whitelist"

// Read-only git subcommands that are safe to whitelist.
const GIT_READONLY_SUBS = new Set([
    "log", "show", "status", "diff", "branch", "remote", "fetch",
    "ls-files", "ls-tree", "rev-parse", "cat-file", "describe",
    "shortlog", "stash", "tag", "blame", "grep", "reflog",
    "name-rev", "for-each-ref", "count-objects", "fsck",
])

// Returns true if the command is a git invocation whose subcommand modifies history/state.
function isDestructiveGitCommand(command: string): boolean {
    const tokens = command.trim().split(/\s+/)
    const prog = tokens[0]?.split("/").pop()
    if (prog !== "git") return false
    // skip flags/values like git -C <dir> <sub>
    let i = 1
    while (tokens[i] && tokens[i].startsWith("-")) i += 2   // skip flag + its value
    let sub = tokens[i]
    return !sub || !GIT_READONLY_SUBS.has(sub)
}

// Multiplexer programs whose first sub-word matters (so we whitelist "dotnet build" not all of
// "dotnet", and "git log" not all of "git").
const MULTIPLEXERS = new Set(["git", "dotnet", "npm", "yarn", "pnpm", "bun", "deno", "docker", "cargo", "go", "kubectl", "pip", "pip3", "brew", "make"])

// Splits a command line into its pipe/sequence segments, respecting quotes. Returns null when it
// contains command substitution ($(...), backticks, <(...)) or unbalanced quotes — those can hide
// arbitrary commands and must always be confirmed rather than auto-classified.
//
// Quote-aware so a "|" inside grep -E "(a|b)" isn't a pipe, and a "&" inside a 2>&1 redirect isn't a
// sequence operator. Splits on | || && ; and bare & (background), only outside quotes.
function commandSegments(command: string): string[] | null {
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
            if (prev === ">" || next === ">") { cur += c; continue }  // part of a redirect (2>&1, &>) — keep
            segments.push(cur); cur = ""; continue                    // bare & — background/sequence
        }
        cur += c
    }
    if (quote) return null   // unbalanced quotes — don't risk mis-parsing
    segments.push(cur)
    return segments.map(s => s.trim()).filter(Boolean)
}

// Tokenise a segment, dropping leading VAR=val assignments.
function segmentTokens(seg: string): string[] {
    return seg.split(/\s+/).filter(t => t && !/^[A-Za-z_][A-Za-z0-9_]*=/.test(t))
}

// The program a segment invokes (basename of the first token, so /usr/bin/grep → grep).
function segmentProgram(seg: string): string {
    const first = segmentTokens(seg)[0] || seg
    return first.split("/").pop() || first
}

// The allowlist key to store when the user whitelists a segment: "program subcommand" for known
// multiplexers (dotnet build), otherwise just the program (grep).
function segmentWhitelistKey(seg: string): string {
    const tokens = segmentTokens(seg)
    const prog   = (tokens[0] || seg).split("/").pop() || seg
    if (MULTIPLEXERS.has(prog) && tokens[1] && !tokens[1].startsWith("-")) return `${prog} ${tokens[1]}`
    return prog
}

// Keys to add when whitelisting a command (one per segment). Null when undecomposable.
function commandWhitelistKeys(command: string): string[] | null {
    const segs = commandSegments(command)
    if (!segs || segs.length === 0) return null
    return [...new Set(segs.map(segmentWhitelistKey).filter(Boolean))]
}

// An allowlist entry matches a segment if it's a single-token PROGRAM name equal to the segment's
// program (e.g. "grep"), or a multi-word PREFIX the segment starts with (e.g. "dotnet build").
function entryMatchesSegment(entry: string, seg: string, prog: string): boolean {
    const e = entry.trim()
    if (!e) return false
    if (/\s/.test(e)) return seg === e || seg.startsWith(e + " ")
    return prog === e
}

// A command is allowed only if EVERY segment is on the allowlist.
// Destructive git commands can never be auto-run; read-only git subcommands can be whitelisted.
function commandIsAllowed(command: string, allowlist: string[]): boolean {
    if (isDestructiveGitCommand(command)) return false
    const segs = commandSegments(command)
    if (!segs || segs.length === 0) return false
    return segs.every(seg => {
        if (isDestructiveGitCommand(seg)) return false
        const prog = segmentProgram(seg)
        return allowlist.some(e => entryMatchesSegment(e, seg, prog))
    })
}

// Formats a finished command for the model: exit code first, then output, tail-biased on
// truncation since compiler/test errors tend to land at the end.
function formatCommandResult(command: string, res: { code: number; stdout: string; stderr: string; timedOut: boolean }): string {
    const MAX = 6000
    const clip = (s: string) => s.length > MAX ? "…(earlier output truncated)…\n" + s.slice(s.length - MAX) : s
    const parts = [`$ ${command}`, `exit code: ${res.code}${res.timedOut ? " (timed out)" : ""}`]
    if (res.stdout.trim()) parts.push("--- stdout ---\n" + clip(res.stdout.trimEnd()))
    if (res.stderr.trim()) parts.push("--- stderr ---\n" + clip(res.stderr.trimEnd()))
    if (!res.stdout.trim() && !res.stderr.trim()) parts.push("(no output)")
    return parts.join("\n\n")
}

const cmdOverlayStyle: CSSProperties = { position: "fixed", inset: 0, background: "rgba(0,0,0,0.55)", display: "flex", alignItems: "center", justifyContent: "center", zIndex: 2000 }
const cmdModalStyle:   CSSProperties = { background: "#1e1e22", border: "1px solid #3a3a40", borderRadius: 10, padding: "20px 22px", width: "min(560px, 90vw)", boxShadow: "0 12px 40px rgba(0,0,0,0.5)", color: "#e8e8ea" }
const cmdTitleStyle:   CSSProperties = { fontSize: 15, fontWeight: 600, marginBottom: 4 }
const cmdSubStyle:     CSSProperties = { fontSize: 12.5, opacity: 0.7, marginBottom: 12 }
const cmdCodeStyle:    CSSProperties = { background: "#111114", border: "1px solid #34343a", borderRadius: 6, padding: "10px 12px", fontFamily: "ui-monospace, SFMono-Regular, Menlo, monospace", fontSize: 13, whiteSpace: "pre-wrap", wordBreak: "break-all", margin: 0, marginBottom: 16 }
const cmdActionsStyle: CSSProperties = { display: "flex", gap: 8, justifyContent: "flex-end" }
const cmdBtnBase:      CSSProperties = { padding: "7px 14px", borderRadius: 6, fontSize: 13, fontWeight: 500, cursor: "pointer", border: "1px solid transparent" }

export default function App() {
    const { threads, load: loadThreads } = useThreads()

    const [activeThread,     setActiveThread]     = useState<string | null>(null)
    const [isInternal,       setIsInternal]        = useState(false)
    const [agentName,        setAgentName]         = useState<string | null>(null)
    const [codeMode,         setCodeMode]          = useState(false)
    const [mode,             setMode]              = useState<AppMode>("idle")
    const [items,            setItems]             = useState<ThreadItem[]>([])
    const [isStreaming,      setIsStreaming]        = useState(false)
    const [isRemembering,    setIsRemembering]      = useState(false)
    const [sidebarCollapsed, setSidebarCollapsed]  = useState(isNarrowPortrait)
    const [pendingAttach,    setPendingAttach]      = useState<PendingAttachment[]>([])
    const [threadAttach,     setThreadAttach]       = useState<Attachment[]>([])
    const [toasts,           setToasts]            = useState<{ id: string; msg: string }[]>([])
    const [activeView,       setActiveView]        = useState<"chat" | "projects">("chat")
    const [projects,         setProjects]          = useState<Project[]>([])
    const [selectedProject,  setSelectedProject]   = useState<string | null>(null)
    const [selectedPipeline, setSelectedPipeline]  = useState<string | null>(null)
    const [speechMode,       setSpeechMode]        = useState(false)
    const [speechCaption,    setSpeechCaption]     = useState<string | null>(null)
    const [speechOrbState,   setSpeechOrbState]    = useState<"listening" | "thinking" | "speaking">("listening")
    const listenerRef = useRef<ListenerHandle | null>(null)
    const [connState, setConnState] = useState<"connecting" | "connected" | "failed">("connecting")
    const pipelines = usePipelines()

    const stopListening = () => {
        listenerRef.current?.stop()
        listenerRef.current = null
        setSpeechCaption(null)
        setSpeechOrbState("listening")
    }

    const [safetyMode,     setSafetyMode]     = useState(false)
    const safetyModeRef = useRef(false)

    // run_command confirmation: pending prompt awaiting a deny/allow/whitelist decision, plus the
    // in-memory allowlist (loaded from the persisted store, extended live by "Whitelist").
    const [pendingCommand, setPendingCommand] = useState<{ command: string; resolve: (d: CommandDecision) => void } | null>(null)
    const commandAllowlistRef = useRef<string[]>([])
    // Generic yes/no confirmation (used for destructive file deletes).
    const [pendingConfirm, setPendingConfirm] = useState<{ title: string; body: string; resolve: (ok: boolean) => void } | null>(null)
    // Mirror of `projects` so stable callbacks (the tool socket) can read the latest list — used to
    // send the selected project's instructions to the backend as the project-rules context block.
    const projectsRef = useRef<Project[]>([])

    const [clientVersion,  setClientVersion]  = useState<string | null>(null)
    const [outdated,       setOutdated]       = useState(false)

    const globalEsRef      = useRef<EventSource | null>(null)
    const abortRef         = useRef<AbortController | null>(null)
    const pendingMsgRef    = useRef<string | null>(null)
    const preSendCountRef  = useRef(0)
    const activeThreadRef  = useRef<string | null>(null)
    const streamingRef      = useRef(false)
    const activeProjectRef  = useRef<string | null>(null)
    const treeInjectedRef   = useRef<Set<string>>(new Set())
    const toolSocketRef     = useRef<WebSocket | null>(null)
    const toolSocketKeyRef  = useRef<string | null>(null)   // threadKey the socket is bound to
    const stopPollRef       = useRef<(() => void) | null>(null)  // stops the active fast-poll loop

    // Keep refs in sync
    useEffect(() => { activeThreadRef.current = activeThread }, [activeThread])
    useEffect(() => { streamingRef.current = isStreaming }, [isStreaming])
    useEffect(() => { safetyModeRef.current = safetyMode }, [safetyMode])

    useEffect(() => {
        env.getCommandAllowlist().then(list => { commandAllowlistRef.current = Array.isArray(list) ? list : [] }).catch(() => {})
    }, [])

    useEffect(() => { projectsRef.current = projects }, [projects])

    // Pin #shell's height to the REAL visible viewport instead of trusting 100dvh alone — some
    // Android WebViews and in-app browsers resize `dvh` unreliably (or late) when the on-screen
    // keyboard opens, which is what left a large dead blank region above the composer instead of
    // the layout shrinking to fit around the keyboard (#168). visualViewport reports the actual
    // visible area directly and fires promptly on keyboard open/close, so mirror it into a CSS
    // custom property #shell reads, with 100dvh as the fallback where visualViewport is unavailable.
    useEffect(() => {
        const vv = window.visualViewport
        if (!vv) return
        const setHeight = () => document.documentElement.style.setProperty("--app-vh", `${vv.height}px`)
        setHeight()
        vv.addEventListener("resize", setHeight)
        return () => vv.removeEventListener("resize", setHeight)
    }, [])

    const heartbeat = useTypingHeartbeat(() => activeThreadRef.current)

    const loadProjects = useCallback(async () => {
        try {
            const res = await fetch("/projects")
            if (res.ok) setProjects(await res.json())
        } catch { /* ignore */ }
    }, [])

    // ── global event stream ───────────────────────────────
    function openGlobalStream() {
        globalEsRef.current?.close()
        const es = openEventStream(
            (data: AppEvent) => {
                switch (data.type) {
                    case "newThread":
                        loadThreads()
                        break
                    case "threadUpdated":
                        loadThreads()
                        // Refresh active thread content when it changes (new message, etc.)
                        if (data.threadKey === activeThreadRef.current && !streamingRef.current)
                            loadHistory(data.threadKey).then(hist => setItems(hist)).catch(() => {})
                        break
                    case "streaming":
                        if (data.threadKey === activeThreadRef.current && !streamingRef.current) {
                            setItems(prev => {
                                for (let i = prev.length - 1; i >= 0; i--) {
                                    if (prev[i].type === "ariResponse" && prev[i].isStreaming) {
                                        const next = [...prev]
                                        next[i] = { ...prev[i], content: data.text ?? "" }
                                        return next
                                    }
                                }
                                return prev
                            })
                        }
                        break
                    case "streamingFinished":
                        loadThreads()
                        playResponseChime()   // issue #63: chime when an Ari response completes
                        if (data.threadKey === activeThreadRef.current && !streamingRef.current)
                            loadHistory(data.threadKey).then(hist => setItems(hist)).catch(() => {})
                        break
                    case "threadDeleted":
                        loadThreads()
                        if (data.threadKey === activeThreadRef.current) {
                            setActiveThread(null); setIsRemembering(false)
                            setMode("idle"); setCodeMode(false); setSpeechMode(false); setItems([])
                        }
                        break
                }
            },
            () => {
                globalEsRef.current?.close(); globalEsRef.current = null
                setTimeout(openGlobalStream, 3000)
            },
        )
        globalEsRef.current = es
    }

    // ── init ─────────────────────────────────────────────
    // Connect to the server, tolerating 503 (still booting) but surfacing a hard failure when the
    // server is unreachable (fetch throws → null) for several attempts, so #68's retry UI can show.
    const connectAndInit = useCallback(async () => {
        setConnState("connecting")
        let failures = 0
        while (true) {
            const res = await fetch("/threads").catch(() => null)
            if (res && res.status !== 503) break                              // server ready
            if (res === null && ++failures >= 5) { setConnState("failed"); return }
            if (res !== null) failures = 0                                    // 503: booting — keep waiting
            await new Promise(r => setTimeout(r, 2000))
        }
        await Promise.all([loadThreads(), loadProjects()])
        openGlobalStream()
        setConnState("connected")
        window.electronBridge?.markReady()
    }, [loadThreads, loadProjects])

    useEffect(() => {
        connectAndInit()
        // Long fallback poll — events drive updates; this is only a safety net.
        const pollId = setInterval(loadThreads, 60_000)

        async function checkVersion(ver: string) {
            const res = await fetch("/api/info/version").catch(() => null)
            if (!res?.ok) return
            const { requiredClientVersion } = await res.json()
            setOutdated(!!requiredClientVersion && requiredClientVersion !== ver)
        }

        env.getVersion().then(async ver => {
            if (!ver) return
            setClientVersion(ver)
            await checkVersion(ver)
            const versionPollId = setInterval(() => checkVersion(ver), 60 * 1000)
            return () => clearInterval(versionPollId)
        })

        return () => { clearInterval(pollId); globalEsRef.current?.close() }
    }, [connectAndInit, loadThreads, loadProjects])

    // ── toast ─────────────────────────────────────────────
    const showToast = useCallback((msg: string) => {
        const id = crypto.randomUUID()
        setToasts(t => [...t, { id, msg }])
        setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 4200)
    }, [])

    // ── activate / reset ──────────────────────────────────
    function activate(instant = false) {
        setMode("active")
        void instant // instant handled via CSS class in Main
    }

    function resetToIdle() {
        setMode("idle")
        setItems([])
        setCodeMode(false)
        setSpeechMode(false)
        stopListening()
    }

    // ── load thread attachments ───────────────────────────
    const refreshThreadAttach = useCallback(async (key: string) => {
        try {
            const res = await fetch(`/threads/${key}/attachments`)
            if (res.ok) setThreadAttach(await res.json())
            else setThreadAttach([])
        } catch { setThreadAttach([]) }
    }, [])


    // ── open thread ───────────────────────────────────────
    // ── File system bridge (Electron + Code mode + project localPath) ─────────────

    const injectFileTree = useCallback(async (threadKey: string, projectId: string) => {
        if (!window.electronBridge) return
        if (treeInjectedRef.current.has(threadKey)) return
        const project = projects.find(p => p.id === projectId)
        if (!project) return
        const localPath = await env.getLocalPath(projectId)
        if (!localPath) return
        try {
            let tree: string[] = await window.electronBridge.getFileTree(localPath)
            const MAX_PATHS = 800
            let truncated = false
            if (tree.length > MAX_PATHS) { tree = tree.slice(0, MAX_PATHS); truncated = true }
            const header = `Project: ${project.name}\nRoot: ${localPath}\nFiles (${tree.length}${truncated ? "+" : ""}):\n`
            const content = header + tree.join("\n") + (truncated ? "\n... (truncated)" : "")
            const res = await fetch(`/threads/${threadKey}/inject-context`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: "_project_tree.txt", content }),
            })
            if (res.ok) treeInjectedRef.current.add(threadKey)
        } catch (e) { console.error("[FileTree] Error:", e) }
    }, [projects])

    // Open a WebSocket to /api/client?threadKey=... so the server registers file tools
    // on the active thread. Handles all incoming tool-call messages from the server.
    const openToolSocket = useCallback(async (threadKey: string, projectId: string) => {
        console.warn(`[ToolSocket] openToolSocket called  threadKey=${threadKey}  projectId=${projectId}  currentBound=${toolSocketKeyRef.current ?? "none"}`)

        // Already bound to this thread
        if (toolSocketKeyRef.current === threadKey) {
            console.warn(`[ToolSocket] Already bound to ${threadKey} — skipping`)
            return
        }

        // Close any previous socket
        toolSocketRef.current?.close()
        toolSocketRef.current  = null
        toolSocketKeyRef.current = null

        if (!window.electronBridge) { console.warn("[ToolSocket] No electronBridge — aborting"); return }
        const localPath = await env.getLocalPath(projectId)
        if (!localPath) { console.warn(`[ToolSocket] No local path for project ${projectId} — aborting`); return }

        const wsProto = window.location.protocol === "https:" ? "wss" : "ws"
        const wsUrl = `${wsProto}://${window.location.host}/api/client?threadKey=${encodeURIComponent(threadKey)}`
        console.warn(`[ToolSocket] Opening WebSocket  url=${wsUrl}`)
        const ws = new WebSocket(wsUrl)
        toolSocketRef.current    = ws
        toolSocketKeyRef.current = threadKey

        // Resolves when the server acknowledges the tree (tools are bound and ready)
        let resolveReady: () => void
        const ready = new Promise<void>(r => { resolveReady = r })

        ws.onopen = async () => {
            console.warn(`[ToolSocket] WebSocket open  threadKey=${threadKey}  localPath=${localPath}`)
            try {
                const tree = await window.electronBridge!.getFileTree(localPath)
                console.warn(`[ToolSocket] Sending tree  files=${tree.length}  threadKey=${threadKey}`)
                // Include threadKey so the server rebinds tools to the active web-* thread
                const projectRules = projectsRef.current.find(p => p.id === projectId)?.instructions ?? ""
                ws.send(JSON.stringify({ type: "tree", root: localPath, tree, threadKey, projectRules }))
            } catch (e) {
                console.error("[ToolSocket] Failed to send tree:", e)
                resolveReady!()
            }
        }

        ws.onmessage = async (evt) => {
            let msg: { type: string; callId?: string; args?: string; count?: number }
            try { msg = JSON.parse(evt.data) } catch { return }

            if (msg.type === "tree_ack") {
                console.warn(`[ToolSocket] tree_ack received  count=${msg.count}  threadKey=${threadKey}  tools ready`)
                resolveReady!()
                return
            }

            const { type, callId, args } = msg
            if (!callId) return

            console.warn(`[ToolSocket] ← Tool request  type=${type}  callId=${callId}`)

            // Parse args JSON (all tools send their params as a JSON string in "args").
            // On malformed JSON we MUST still reply — a silent return leaves the backend waiting the
            // full tool timeout (~90s) and the model stuck. Send an error so it can retry cleanly.
            let params: Record<string, string> = {}
            try { if (args) params = JSON.parse(args) }
            catch {
                console.warn(`[ToolSocket] → file_error (bad args JSON)  callId=${callId}`)
                ws.send(JSON.stringify({ type: "file_error", callId, error: "Tool arguments were not valid JSON. Re-issue the call as a single well-formed JSON function call (escape quotes/newlines inside string values)." }))
                return
            }

            // Strip surrounding quotes the model sometimes wraps around path values
            if (params.path) params.path = params.path.replace(/^["']+|["']+$/g, "").trim()

            try {
                let result: string

                if (type === "read_file") {
                    const content = await window.electronBridge!.readFile(localPath, params.path ?? "")
                    const all     = content.split("\n")
                    const total   = all.length

                    // Honour start_line/end_line (1-indexed, inclusive). When neither is given, cap
                    // whole-file reads of large files so a single read can't blow the context window
                    // (a 93 KB file is ~30k tokens). The model is told to read a range or search instead.
                    const rawStart = Number((params as unknown as { start_line?: unknown }).start_line)
                    const rawEnd   = Number((params as unknown as { end_line?: unknown }).end_line)
                    const explicit = Number.isFinite(rawStart) || Number.isFinite(rawEnd)
                    let start = Number.isFinite(rawStart) && rawStart > 0 ? Math.min(Math.trunc(rawStart), total) : 1
                    let end   = Number.isFinite(rawEnd)   && rawEnd   > 0 ? Math.min(Math.trunc(rawEnd),   total) : total
                    if (end < start) end = start

                    const READ_MAX_LINES = 1500
                    const READ_MAX_CHARS = 60000
                    let capped = false
                    if (!explicit && total > 0) {
                        let chars = 0, lim = total
                        for (let i = 0; i < total; i++) {
                            chars += all[i].length + 1
                            if (i + 1 >= READ_MAX_LINES || chars >= READ_MAX_CHARS) { lim = i + 1; break }
                        }
                        if (lim < total) { end = lim; capped = true }
                    }

                    // Number lines from their real position so edit_file line ranges and snippets line up.
                    const slice    = all.slice(start - 1, end)
                    const numbered = slice.map((l, i) => `${String(start + i).padStart(6)}: ${l}`).join("\n")
                    const header   = (start === 1 && end === total)
                        ? `[file: "${params.path}" (${total} lines)]`
                        : `[file: "${params.path}" lines ${start}-${end} of ${total}]`
                    const capNote  = capped
                        ? `\n[File is large (${total} lines) — only the first ${end} are shown. Read a specific range with start_line/end_line, or use search_files to find what you need, rather than reading the whole file.]`
                        : ""
                    result = `${header}\n\`\`\`\n${numbered}\n\`\`\`${capNote}`
                    console.warn(`[ToolSocket] → file_content  callId=${callId}  bytes=${result.length}  lines=${start}-${end}/${total}${capped ? " CAPPED" : ""}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "list_directory") {
                    const entries = await window.electronBridge!.listDirectory(localPath, params.path)
                    result = `[directory: "${params.path ?? "."}"]\n${entries.join("\n")}`
                    console.warn(`[ToolSocket] → file_content (list_directory)  callId=${callId}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "search_files") {
                    const ic = String((params as unknown as { ignore_case?: unknown }).ignore_case) === "true"
                        || (params as unknown as { ignore_case?: unknown }).ignore_case === true
                    const matches = await window.electronBridge!.searchFiles(localPath, params.pattern, params.path, params.glob, ic)
                    result = matches.length === 0
                        ? `No matches found for "${params.pattern}".`
                        : `[search: "${params.pattern}"]\n${matches.join("\n")}`
                    console.warn(`[ToolSocket] → file_content (search_files)  callId=${callId}  matches=${matches.length}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "edit_file") {
                    // edit_file edits BY LINE NUMBER — a single start_line/end_line/new_string, or a
                    // MultiEdit-style batch via `edits` (each {start_line, end_line, new_string}).
                    const rawEdits = (params as unknown as { edits?: { new_string?: string; start_line?: number; end_line?: number }[] }).edits
                    const editsArr = Array.isArray(rawEdits) && rawEdits.length > 0 ? rawEdits : null
                    if (safetyModeRef.current) {
                        const diff = editsArr
                            ? editsArr.map((e, i) => `--- edit ${i + 1} ---\n${buildSafetyDiff(e.new_string ?? "", e.start_line, e.end_line)}`).join("\n\n")
                            : buildSafetyDiff(
                                params.new_string ?? "",
                                Number((params as unknown as { start_line?: unknown }).start_line) || undefined,
                                Number((params as unknown as { end_line?: unknown }).end_line) || undefined)
                        console.warn(`[ToolSocket] → file_content (edit_file BLOCKED by safety)  callId=${callId}`)
                        ws.send(JSON.stringify({ type: "file_error", callId, error: `SAFETY MODE — file was NOT modified. Do not call edit_file or write_file again. Respond to the user now: tell them safety mode is on, show the proposed changes as a code block, and say they can disable safety mode (shield icon) to apply them.\n\nProposed diff for ${params.path}:\n\n${diff}` }))
                    } else {
                        const sl = Number((params as unknown as { start_line?: unknown }).start_line)
                        const el = Number((params as unknown as { end_line?: unknown }).end_line)
                        const res = await window.electronBridge!.editFile(localPath, params.path, params.new_string, {
                            edits: editsArr ?? undefined,
                            startLine: Number.isFinite(sl) ? sl : undefined,
                            endLine:   Number.isFinite(el) ? el : undefined,
                        })
                        if (res.ok) {
                            // fs.editFile returns a full message with a numbered snippet around the
                            // edit, so the model sees the new state without re-reading the file.
                            console.warn(`[ToolSocket] → file_content (edit_file)  callId=${callId}  path=${params.path}`)
                            ws.send(JSON.stringify({ type: "file_content", callId, content: res.message ?? `Successfully edited ${params.path}.` }))
                        } else {
                            console.warn(`[ToolSocket] → file_error (edit_file)  callId=${callId}  error=${res.error}`)
                            ws.send(JSON.stringify({ type: "file_error", callId, error: res.error ?? "Edit failed." }))
                        }
                    }

                } else if (type === "write_file") {
                    if (safetyModeRef.current) {
                        const lines = (params.content ?? "").split("\n")
                        console.warn(`[ToolSocket] → file_content (write_file BLOCKED by safety)  callId=${callId}`)
                        ws.send(JSON.stringify({ type: "file_error", callId, error: `SAFETY MODE — file was NOT written. Do not call edit_file or write_file again. Respond to the user now: tell them safety mode is on, show the proposed content as a code block, and say they can disable safety mode (shield icon) to apply it.\n\nProposed content for ${params.path} (${lines.length} lines):\n\n\`\`\`\n${params.content ?? ""}\n\`\`\`` }))
                    } else {
                        await window.electronBridge!.writeFile(localPath, params.path, params.content ?? "")
                        const writtenLines = (params.content ?? "").split("\n").length
                        console.warn(`[ToolSocket] → file_content (write_file)  callId=${callId}  path=${params.path}`)
                        ws.send(JSON.stringify({ type: "file_content", callId, content: `Successfully wrote ${params.path} (${writtenLines} lines).` }))
                    }

                } else if (type === "run_command") {
                    const command = (params.command ?? "").trim()
                    if (!command) {
                        ws.send(JSON.stringify({ type: "file_error", callId, error: "No command provided." }))
                    } else {
                        // Auto-run if allowlisted; otherwise ask the user (deny / allow once / whitelist).
                        let decision: CommandDecision = "allow"
                        if (!commandIsAllowed(command, commandAllowlistRef.current)) {
                            console.warn(`[ToolSocket] run_command needs confirmation  callId=${callId}  cmd=${command}`)
                            decision = await new Promise<CommandDecision>(resolve => setPendingCommand({ command, resolve }))
                        }

                        if (decision === "deny") {
                            console.warn(`[ToolSocket] → file_error (run_command DENIED)  callId=${callId}`)
                            ws.send(JSON.stringify({ type: "file_error", callId, error: `The user denied permission to run \`${command}\`. Do not try to run it again. Continue without it, or ask the user how they would like to proceed.` }))
                        } else {
                            if (decision === "whitelist") {
                                // Whitelist by program (or "program subcommand" for multiplexers like
                                // dotnet), so future invocations with different args/redirections —
                                // and chains of already-trusted programs — auto-run.
                                const keys = (commandWhitelistKeys(command) ?? []).filter(k => k && !isDestructiveGitCommand(k))
                                const next = [...commandAllowlistRef.current]
                                for (const k of keys) if (!next.includes(k)) next.push(k)
                                if (next.length !== commandAllowlistRef.current.length) {
                                    commandAllowlistRef.current = next
                                    try { await env.setCommandAllowlist(next) } catch { /* ignore persist failure */ }
                                }
                            }
                            console.warn(`[ToolSocket] → run_command EXEC  callId=${callId}  cmd=${command}`)
                            const cmdRes = await window.electronBridge!.runCommand(localPath, command)
                            ws.send(JSON.stringify({ type: "file_content", callId, content: formatCommandResult(command, cmdRes) }))
                        }
                    }

                } else if (type === "find_files") {
                    const files = await window.electronBridge!.findFiles(localPath, params.pattern, params.path)
                    result = files.length === 0
                        ? `No files found matching "${params.pattern}".`
                        : `[find: "${params.pattern}"]\n${files.join("\n")}`
                    console.warn(`[ToolSocket] → file_content (find_files)  callId=${callId}  count=${files.length}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "delete_file") {
                    // Destructive — always ask the user before deleting.
                    const ok = await new Promise<boolean>(resolve => setPendingConfirm({
                        title: "Delete this file?",
                        body:  params.path ?? "",
                        resolve,
                    }))
                    if (!ok) {
                        console.warn(`[ToolSocket] → file_error (delete_file DENIED)  callId=${callId}`)
                        ws.send(JSON.stringify({ type: "file_error", callId, error: `The user denied deleting ${params.path}. Do not try to delete it again; ask how they would like to proceed.` }))
                    } else {
                        const res = await window.electronBridge!.deleteFile(localPath, params.path)
                        if (res.ok) ws.send(JSON.stringify({ type: "file_content", callId, content: `Deleted ${params.path}.` }))
                        else        ws.send(JSON.stringify({ type: "file_error", callId, error: res.error ?? "Delete failed." }))
                    }

                } else if (type === "move_file") {
                    const res = await window.electronBridge!.moveFile(localPath, params.source, params.destination)
                    if (res.ok) ws.send(JSON.stringify({ type: "file_content", callId, content: `Moved ${params.source} → ${params.destination}.` }))
                    else        ws.send(JSON.stringify({ type: "file_error", callId, error: res.error ?? "Move failed." }))
                } else if (type === "preview_file") {
                    // The client no longer builds the outline. It returns RAW file content tagged
                    // "[rawpreview] <bytes>"; the server builds the class-diagram outline with the single
                    // shared C# extractor (PreviewFormatter) so there is no JS/C# divergence (#138).
                    const content = await window.electronBridge!.readFile(localPath, params.path ?? "")
                    const bytes   = new TextEncoder().encode(content).length
                    result = `[rawpreview] ${bytes}\n${content}`
                    console.warn(`[ToolSocket] → file_content (preview_file raw)  callId=${callId}  path=${params.path}  bytes=${bytes}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))
                }
            } catch (e: unknown) {
                const msg = e instanceof Error ? e.message : String(e)
                console.warn(`[ToolSocket] → file_error  callId=${callId}  error=${msg}`)
                ws.send(JSON.stringify({ type: "file_error", callId, error: msg }))
            }
        }

        ws.onerror = (e) => { console.warn(`[ToolSocket] WebSocket error  threadKey=${threadKey}`, e); resolveReady!() }

        ws.onclose = () => {
            console.warn(`[ToolSocket] WebSocket closed  threadKey=${threadKey}`)
            if (toolSocketKeyRef.current === threadKey) {
                toolSocketRef.current    = null
                toolSocketKeyRef.current = null
            }
        }

        return ready
    }, [])

    const openThread = useCallback(async (
        key: string, internal = false, agName: string | null = null, isCode = false,
        projectId: string | null = null,
    ) => {
        abortRef.current?.abort(); abortRef.current = null
        stopPollRef.current?.(); stopPollRef.current = null
        listenerRef.current?.stop(); listenerRef.current = null; setSpeechCaption(null)

        setActiveThread(key)
        setIsInternal(internal)
        setAgentName(agName)
        setCodeMode(isCode)
        setSpeechMode(false)
        setIsRemembering(false)
        activeProjectRef.current = projectId
        setSelectedProject(projectId)

        // Fetch the thread (state + history) via the new polling endpoint
        const detail = await fetchThread(key).catch(() => null)
        setSpeechMode(detail?.pipeline === "speech")
        const hist = detail?.history ?? await loadHistory(key, internal).catch(() => [])
        setItems(hist)
        activate(hist.length > 0)

        // If the thread is already streaming (e.g. user switches to it mid-generation),
        // start fast-poll so the view stays live without waiting for the next SSE event.
        if (detail?.state === "streaming") {
            setIsStreaming(true)
            stopPollRef.current = pollThreadWhileStreaming(key, d => {
                if (activeThreadRef.current !== key) return
                setItems(d.history)  // server already excludes cancelled items
                if (d.state !== "streaming") {
                    setIsStreaming(false)
                    loadThreads()
                    stopPollRef.current = null
                }
            })
        } else {
            setIsStreaming(false)
        }

        if (!internal) {
            await refreshThreadAttach(key)
            if (projectId) {
                await injectFileTree(key, projectId)
                await openToolSocket(key, projectId)
            }
        }
    }, [refreshThreadAttach, injectFileTree, openToolSocket, loadThreads])

    // Deep-link from a push notification: /?thread=KEY opens that thread once the app is connected.
    const deepLinkedRef = useRef(false)
    useEffect(() => {
        if (connState !== "connected" || deepLinkedRef.current) return
        const key = new URLSearchParams(window.location.search).get("thread")
        if (!key) return
        deepLinkedRef.current = true
        openThread(key).catch(() => {})
        // Strip the param so a later refresh doesn't re-open it.
        window.history.replaceState({}, "", window.location.pathname)
    }, [connState, openThread])

    // Create a Speech thread, open it into the orb view, and start streaming the mic to ARI.Listener.
    const beginSpeech = useCallback(async () => {
        const key = await createThread(selectedProject, "speech")
        await openThread(key, false, null, false, null) // clears any previous listener
        loadThreads()
        try {
            listenerRef.current = await startListening(key, e => {
                switch (e.type) {
                    case "transcript":
                        if (e.text) setSpeechCaption(e.addressed ? e.text : `(overheard) ${e.text}`)
                        break
                    case "thinking":  setSpeechOrbState("thinking"); break
                    case "speaking":  setSpeechOrbState("speaking"); break
                    case "say":       if (e.text) setSpeechCaption(e.text); break  // Ari's reply
                    case "done":      setSpeechOrbState("listening"); break
                }
            })
        } catch (err) {
            console.warn("[Listener] mic start failed", err)
            setSpeechCaption("Microphone unavailable")
        }
    }, [selectedProject, openThread, loadThreads])

    // ── new chat ──────────────────────────────────────────
    function newChat() {
        abortRef.current?.abort(); abortRef.current = null
        stopPollRef.current?.(); stopPollRef.current = null
        toolSocketRef.current?.close(); toolSocketRef.current = null; toolSocketKeyRef.current = null
        setActiveThread(null); setIsInternal(false); setAgentName(null)
        setCodeMode(false); setSpeechMode(false); setIsStreaming(false); setIsRemembering(false)
        stopListening()
        setPendingAttach([]); setThreadAttach([])
        // Preserve selectedProject so repeated new chats on the same project
        // don't require re-selecting it each time.
        setSelectedProject(activeProjectRef.current)
        activeProjectRef.current = null
        setActiveView("chat")
        resetToIdle()
    }

    // ── send ──────────────────────────────────────────────
    const send = useCallback(async (prompt: string) => {
        if (!prompt && pendingAttach.length === 0) return

        const STOP_WORDS = ["stop", "wait", "escape"]
        if (isStreaming && STOP_WORDS.includes(prompt.toLowerCase().trim())) {
            if (activeThreadRef.current) await cancelProcessing(activeThreadRef.current)
            return
        }
        if (isStreaming) {
            abortRef.current?.abort(); abortRef.current = null
            setIsStreaming(false)
            pendingMsgRef.current = null
        }

        const needsNew = !activeThreadRef.current

        if (needsNew && !prompt.startsWith("/")) {
            activate()
            const optimisticAttach = pendingAttach.length ? [...pendingAttach] : undefined
            setPendingAttach([])
            pendingMsgRef.current = prompt
            preSendCountRef.current = items.length
            const userItem: ThreadItem = {
                type: "userMessage", content: prompt,
                timestamp: new Date().toISOString(),
                attachments: optimisticAttach as Attachment[] | undefined,
            }
            setItems(prev => [...prev, userItem, {
                type: "ariResponse", content: "",
                timestamp: new Date().toISOString(),
                isStreaming: true,
            }])
            setIsStreaming(true)
        }

        let key = activeThreadRef.current
        if (needsNew) {
            key = await createThread(selectedProject, selectedPipeline)
            setActiveThread(key)
            activeProjectRef.current = selectedProject
            loadThreads()
            if (selectedProject) {
                // A project thread is a code thread — flip code mode on now so the
                // dark-mode overlay animates in (false → true while #main is mounted)
                // instead of only appearing when the thread is later reopened.
                setCodeMode(true)
                await injectFileTree(key, selectedProject)
            }
        } else if (mode === "idle") {
            activate()
        }

        // Always ensure tools are bound before sending — covers both new threads and
        // existing threads opened after a server restart (openToolSocket no-ops if already bound)
        const project = selectedProject ?? activeProjectRef.current
        let localPath: string | null = null
        if (project) {
            await openToolSocket(key!, project)
            localPath = await env.getLocalPath(project)
        }

        if (prompt.startsWith("/")) {
            try {
                const res = await fetch("/commands", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ threadKey: key, input: prompt }),
                })
                if (!res.ok) {
                    const data = await res.json()
                    setItems(prev => [...prev,
                        { type: "commandInput", input: prompt, timestamp: new Date().toISOString(), content: "" },
                        { type: "commandResponse", response: data.error ?? "Command failed.", timestamp: new Date().toISOString(), content: "" },
                    ])
                }
            } catch {
                setItems(prev => [...prev,
                    { type: "commandInput", input: prompt, timestamp: new Date().toISOString(), content: "" },
                    { type: "commandResponse", response: "Command failed — could not reach A·R·I.", timestamp: new Date().toISOString(), content: "" },
                ])
            }
            return
        }

        if (!needsNew) {
            const optimisticAttach = pendingAttach.length ? [...pendingAttach] : undefined
            setPendingAttach([])
            pendingMsgRef.current = null
            preSendCountRef.current = items.length
            setItems(prev => [...prev,
                {
                    type: "userMessage", content: prompt,
                    timestamp: new Date().toISOString(),
                    attachments: optimisticAttach as Attachment[] | undefined,
                },
                {
                    type: "ariResponse", content: "",
                    timestamp: new Date().toISOString(),
                    isStreaming: true,
                },
            ])
            setIsStreaming(true)
        }

        const ctrl = new AbortController()
        abortRef.current = ctrl
        const keyForStream = key!

        async function runStream() {
            try {
                const resp = await fetch(`/threads/${keyForStream}/stream`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(localPath ? { prompt, localPath } : { prompt }),
                    signal: ctrl.signal,
                })

                const reader  = resp.body!.getReader()
                const decoder = new TextDecoder()
                let buf = ""

                function handleLine(line: string) {
                    if (!line.startsWith("data: ")) return
                    const data = line.slice(6)
                    if (activeThreadRef.current !== keyForStream) { ctrl.abort(); return }

                    if (data === "[DONE]") {
                        abortRef.current = null
                        setIsStreaming(false)
                        loadThreads()
                        // Replace the optimistic streaming item with the finalized server history
                        fetchThread(keyForStream).then(detail => {
                            if (!detail) return loadHistory(keyForStream).then(hist => { if (activeThreadRef.current === keyForStream) setItems(hist) }).catch(() => {})
                            if (activeThreadRef.current === keyForStream) setItems(detail.history)
                        }).catch(() => {})
                        return
                    }
                    if (data === "[CANCELLED]") {
                        abortRef.current = null
                        setIsStreaming(false)
                        setItems(prev => prev.slice(0, preSendCountRef.current))
                        return
                    }
                    if (data.startsWith("[ERROR]")) {
                        setItems(prev => {
                            const last = prev[prev.length - 1]
                            if (last?.type === "ariResponse" && last.isStreaming)
                                return [...prev.slice(0, -1), { ...last, content: data.replace("[ERROR] ", ""), isStreaming: false }]
                            return [...prev, { type: "ariResponse", content: data.replace("[ERROR] ", ""), timestamp: new Date().toISOString() }]
                        })
                        abortRef.current = null; setIsStreaming(false)
                        return
                    }
                    const text = data.replace(/\\n/g, "\n")

                    // Update the last ariResponse item in place (it was pre-added with isStreaming: true)
                    setItems(prev => {
                        const last = prev[prev.length - 1]
                        if (!last || last.type !== "ariResponse") return prev
                        return [...prev.slice(0, -1), { ...last, content: text, isStreaming: true }]
                    })
                }

                while (true) {
                    const { done, value } = await reader.read()
                    if (done) break
                    buf += decoder.decode(value, { stream: true })
                    const lines = buf.split("\n")
                    buf = lines.pop()!
                    for (const line of lines) handleLine(line.trimEnd())
                }
                if (buf) handleLine(buf.trimEnd())
            } catch (err: unknown) {
                if (err instanceof Error && err.name === "AbortError") return
                setItems(prev => {
                    const last = prev[prev.length - 1]
                    if (last?.type === "ariResponse" && last.isStreaming)
                        return [...prev.slice(0, -1), { ...last, content: "[connection error]", isStreaming: false }]
                    return [...prev, { type: "ariResponse", content: "[connection error]", timestamp: new Date().toISOString() }]
                })
                abortRef.current = null; setIsStreaming(false)
            }
        }

        runStream()
    }, [isStreaming, pendingAttach, items.length, mode, loadThreads, selectedProject, injectFileTree, openToolSocket])

    // The plan-proposed card's "Accept & Build" button calls this global — it sends the deterministic
    // "[approve-plan]" signal, which the coding pipeline reads as approval (→ Development with the payload).
    useEffect(() => {
        (window as unknown as { __ariApprovePlan?: () => void }).__ariApprovePlan = () => send("[approve-plan]")
    }, [send])

    // ── upload thread attachment ──────────────────────────
    const uploadThreadFiles = useCallback(async (files: File[]) => {
        let key = activeThreadRef.current
        if (!key) { key = await createThread(); setActiveThread(key); activate() }

        const succeeded: string[] = []
        for (const file of files) {
            const fd = new FormData(); fd.append("file", file)
            const res = await fetch(`/threads/${key}/attachments`, { method: "POST", body: fd })
            if (res.ok) succeeded.push(file.name)
            else { const err = await res.json().catch(() => null); showToast(err?.error ?? `Could not attach ${file.name}.`) }
        }
        if (succeeded.length) await refreshThreadAttach(key)
        return succeeded
    }, [refreshThreadAttach, showToast])

    const removeThreadAttachment = useCallback(async (name: string) => {
        if (!activeThreadRef.current) return
        await fetch(`/threads/${activeThreadRef.current}/attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        await refreshThreadAttach(activeThreadRef.current)
    }, [refreshThreadAttach])

    // ── upload message attachment ─────────────────────────
    const uploadMessageFiles = useCallback(async (files: File[]) => {
        let key = activeThreadRef.current
        if (!key) { key = await createThread(); setActiveThread(key) }

        const uploading = files.map(f => ({
            name: f.name, isImage: false, mimeType: null, content: null, uploading: true,
        }))
        setPendingAttach(prev => {
            const filtered = prev.filter(a => !files.some(f => f.name === a.name))
            return [...filtered, ...uploading]
        })

        for (const file of files) {
            const fd = new FormData(); fd.append("file", file)
            const res = await fetch(`/threads/${key}/message-attachments`, { method: "POST", body: fd })
            if (res.ok) {
                const data = await res.json()
                setPendingAttach(prev => [
                    ...prev.filter(a => a.name !== data.name),
                    { name: data.name, isImage: data.isImage, mimeType: data.mimeType, content: data.content, uploading: false },
                ])
            } else {
                setPendingAttach(prev => prev.filter(a => a.name !== file.name))
                const err = await res.json().catch(() => null)
                showToast(err?.error ?? `Could not attach ${file.name}.`)
            }
        }
    }, [showToast])

    const removeMessageAttachment = useCallback(async (name: string) => {
        if (!activeThreadRef.current) return
        await fetch(`/threads/${activeThreadRef.current}/message-attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        setPendingAttach(prev => prev.filter(a => a.name !== name))
    }, [])

    const isWin32 = window.electronBridge?.platform === "win32"

    return (
        <div id="shell">
            {connState === "failed" && (
                <div id="conn-error-overlay">
                    <div id="conn-error-card">
                        <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/>
                            <line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
                        </svg>
                        <p id="conn-error-title">Cannot connect to server</p>
                        <button id="conn-error-retry" onClick={() => connectAndInit()}>Retry</button>
                    </div>
                </div>
            )}
            {window.electronBridge && (
                <div id="titlebar-drag">
                    {isWin32 && (
                        <div id="win-controls">
                            <button id="win-minimize-btn" title="Minimise" onMouseDown={e => e.stopPropagation()} onClick={() => window.electronBridge!.minimizeWindow()}>
                                <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"><line x1="1" y1="5" x2="9" y2="5"/></svg>
                            </button>
                            <button id="win-maximize-btn" title="Maximise" onMouseDown={e => e.stopPropagation()} onClick={() => window.electronBridge!.maximizeWindow()}>
                                <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1.5"><rect x="1" y="1" width="8" height="8" rx="1"/></svg>
                            </button>
                            <button id="win-close-btn" title="Close" onMouseDown={e => e.stopPropagation()} onClick={() => window.electronBridge!.closeWindow()}>
                                <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"><line x1="1" y1="1" x2="9" y2="9"/><line x1="9" y1="1" x2="1" y2="9"/></svg>
                            </button>
                        </div>
                    )}
                </div>
            )}
            <Sidebar
                threads={threads}
                activeThread={activeThread}
                activeView={activeView}
                onNewChat={newChat}
                onOpenProjects={() => setActiveView("projects")}
                onSelectThread={(t: ThreadEntry) => { setActiveView("chat"); openThread(t.key, t.isInternal, t.agentName, t.isCodeMode, t.projectId ?? null) }}
                onCloseThread={(t: ThreadEntry) => { void closeThread(t.key) }}
                collapsed={sidebarCollapsed}
                onToggleCollapse={() => setSidebarCollapsed(c => !c)}
                clientVersion={clientVersion}
                outdated={outdated}
            />
            <div id="sidebar-overlay"
                className={sidebarCollapsed ? "" : ""}
                onClick={() => setSidebarCollapsed(true)}
            />
            {activeView === "projects" ? (
                <ProjectsPage projects={projects} onProjectCreated={loadProjects} />
            ) : (
                <Main
                    mode={mode}
                    codeMode={codeMode}
                    items={items}
                    planProposed={!isStreaming && items[items.length - 1]?.type === "ariResponse"
                        && (items[items.length - 1]?.blocks ?? []).some(b => (b as { type?: string }).type === "plan")}
                    isRemembering={isRemembering}
                    isStreaming={isStreaming}
                    activeThread={activeThread}
                    isInternal={isInternal}
                    agentName={agentName}
                    sidebarCollapsed={sidebarCollapsed}
                    onOpenSidebar={() => setSidebarCollapsed(false)}
                    pendingAttach={pendingAttach}
                    threadAttach={threadAttach}
                    onSend={send}
                    onUploadThreadFiles={uploadThreadFiles}
                    onUploadMessageFiles={uploadMessageFiles}
                    onRemoveThreadAttach={removeThreadAttachment}
                    onRemoveMessageAttach={removeMessageAttachment}
                    onHeartbeatStart={heartbeat.start}
                    onHeartbeatStop={heartbeat.stop}
                    safetyMode={safetyMode}
                    onToggleSafety={() => setSafetyMode(m => !m)}
                    commands={COMMANDS}
                    projects={projects}
                    selectedProject={selectedProject}
                    onProjectChange={setSelectedProject}
                    pipelines={pipelines}
                    selectedPipeline={selectedPipeline}
                    onPipelineChange={setSelectedPipeline}
                    speechMode={speechMode}
                    onBeginSpeech={beginSpeech}
                    speechCaption={speechCaption}
                    speechOrbState={speechOrbState}
                />
            )}
            {toasts.map(t => (
                <div key={t.id} className="toast toast-visible">{t.msg}</div>
            ))}
            {pendingCommand && (
                <div style={cmdOverlayStyle}
                     onClick={() => { pendingCommand.resolve("deny"); setPendingCommand(null) }}>
                    <div style={cmdModalStyle} onClick={e => e.stopPropagation()}>
                        <div style={cmdTitleStyle}>Run this command?</div>
                        <div style={cmdSubStyle}>
                            {isDestructiveGitCommand(pendingCommand.command)
                                ? "ARI wants to run a git command that modifies history. This always requires your approval."
                                : "ARI wants to run a command that isn't on the allow list."}
                        </div>
                        <pre style={cmdCodeStyle}>{pendingCommand.command}</pre>
                        <div style={cmdActionsStyle}>
                            <button style={{ ...cmdBtnBase, background: "transparent", borderColor: "#5a5a62", color: "#e8e8ea" }}
                                    onClick={() => { pendingCommand.resolve("deny"); setPendingCommand(null) }}>Deny</button>
                            <button style={{ ...cmdBtnBase, background: "#2d6cdf", color: "#fff" }}
                                    onClick={() => { pendingCommand.resolve("allow"); setPendingCommand(null) }}>Allow once</button>
                            {!isDestructiveGitCommand(pendingCommand.command) && (() => {
                                const progs = commandWhitelistKeys(pendingCommand.command)
                                if (!progs || progs.length === 0) return null  // command substitution — allow-once only
                                return (
                                    <button style={{ ...cmdBtnBase, background: "#1f9d57", color: "#fff" }}
                                            onClick={() => { pendingCommand.resolve("whitelist"); setPendingCommand(null) }}>
                                        Always allow {progs.join(", ")}
                                    </button>
                                )
                            })()}
                        </div>
                    </div>
                </div>
            )}
            {pendingConfirm && (
                <div style={cmdOverlayStyle}
                     onClick={() => { pendingConfirm.resolve(false); setPendingConfirm(null) }}>
                    <div style={cmdModalStyle} onClick={e => e.stopPropagation()}>
                        <div style={cmdTitleStyle}>{pendingConfirm.title}</div>
                        <pre style={cmdCodeStyle}>{pendingConfirm.body}</pre>
                        <div style={cmdActionsStyle}>
                            <button style={{ ...cmdBtnBase, background: "transparent", borderColor: "#5a5a62", color: "#e8e8ea" }}
                                    onClick={() => { pendingConfirm.resolve(false); setPendingConfirm(null) }}>Cancel</button>
                            <button style={{ ...cmdBtnBase, background: "#c0392b", color: "#fff" }}
                                    onClick={() => { pendingConfirm.resolve(true); setPendingConfirm(null) }}>Delete</button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    )
}
