import { useState, useEffect, useCallback, useRef } from "react"
import Sidebar from "./components/Sidebar"
import Main from "./components/Main"
import ProjectsPage from "./components/ProjectsPage"
import {
    useThreads, createThread, loadHistory, openWatchStream, cancelProcessing,
    useTypingHeartbeat,
    type ThreadItem, type ThreadEntry, type WatchEvent, type Attachment, type Project,
} from "./hooks/useThreads"
import { env } from "./env"
import "./styles/app.css"

export type AppMode = "idle" | "active"

function buildSafetyDiff(oldStr: string, newStr: string): string {
    const removed = oldStr.split("\n").map(l => `- ${l}`).join("\n")
    const added   = newStr.split("\n").map(l => `+ ${l}`).join("\n")
    return `\`\`\`diff\n${removed}\n${added}\n\`\`\``
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
    const [sidebarCollapsed, setSidebarCollapsed]  = useState(false)
    const [pendingAttach,    setPendingAttach]      = useState<PendingAttachment[]>([])
    const [threadAttach,     setThreadAttach]       = useState<Attachment[]>([])
    const [toasts,           setToasts]            = useState<{ id: string; msg: string }[]>([])
    const [activeView,       setActiveView]        = useState<"chat" | "projects">("chat")
    const [projects,         setProjects]          = useState<Project[]>([])
    const [selectedProject,  setSelectedProject]   = useState<string | null>(null)

    const [safetyMode,     setSafetyMode]     = useState(false)
    const safetyModeRef = useRef(false)

    const [clientVersion,  setClientVersion]  = useState<string | null>(null)
    const [outdated,       setOutdated]       = useState(false)

    const watchEsRef  = useRef<EventSource | null>(null)
    const abortRef    = useRef<AbortController | null>(null)
    const pendingMsgRef    = useRef<string | null>(null)
    const preSendCountRef  = useRef(0)
    const watchRenderedRef = useRef(false)
    const activeThreadRef   = useRef<string | null>(null)
    const streamingRef      = useRef(false)
    const activeProjectRef  = useRef<string | null>(null)
    const treeInjectedRef   = useRef<Set<string>>(new Set())
    const toolSocketRef     = useRef<WebSocket | null>(null)
    const toolSocketKeyRef  = useRef<string | null>(null)   // threadKey the socket is bound to

    // Keep refs in sync
    useEffect(() => { activeThreadRef.current = activeThread }, [activeThread])
    useEffect(() => { streamingRef.current = isStreaming }, [isStreaming])
    useEffect(() => { safetyModeRef.current = safetyMode }, [safetyMode])

    const heartbeat = useTypingHeartbeat(() => activeThreadRef.current)

    const loadProjects = useCallback(async () => {
        try {
            const res = await fetch("/api/projects")
            if (res.ok) setProjects(await res.json())
        } catch { /* ignore */ }
    }, [])

    // ── init ─────────────────────────────────────────────
    useEffect(() => {
        async function waitAndInit() {
            while (true) {
                const res = await fetch("/api/threads").catch(() => null)
                if (res && res.status !== 503) break
                await new Promise(r => setTimeout(r, 2000))
            }
            await Promise.all([loadThreads(), loadProjects()])
            window.electronBridge?.markReady()
        }
        waitAndInit()
        const pollId = setInterval(loadThreads, 5000)

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

        return () => clearInterval(pollId)
    }, [loadThreads, loadProjects])

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
    }

    // ── load thread attachments ───────────────────────────
    const refreshThreadAttach = useCallback(async (key: string) => {
        try {
            const res = await fetch(`/api/threads/${key}/attachments`)
            if (res.ok) setThreadAttach(await res.json())
            else setThreadAttach([])
        } catch { setThreadAttach([]) }
    }, [])

    // ── watch connection ──────────────────────────────────
    const openWatch = useCallback((key: string, agName: string | null) => {
        watchRenderedRef.current = false
        watchEsRef.current?.close()

        const es = openWatchStream(
            key,
            async (data: WatchEvent) => {
                if (activeThreadRef.current !== key) { es.close(); return }
                if (data.deleted) {
                    es.close(); watchEsRef.current = null
                    setActiveThread(null)
                    setIsRemembering(false)
                    setMode("idle")
                    setCodeMode(false)
                    setItems([])
                    await loadThreads()
                    return
                }
                const targetCodeMode = data.isCodeMode ?? false
                setCodeMode(targetCodeMode)
                if (!streamingRef.current) {
                    const hist = await loadHistory(key, false).catch(() => null)
                    if (hist) {
                        setItems(hist)
                        const hasResponse = hist.some(i => i.type === "ariResponse")
                        if (hasResponse) watchRenderedRef.current = true
                    }
                    setIsRemembering(!!(data.isRemembering && !data.isProcessing))
                }
            },
            () => {
                if (activeThreadRef.current !== key) return
                es.close(); watchEsRef.current = null
                setTimeout(() => {
                    if (activeThreadRef.current === key)
                        openWatch(key, agName)
                }, 3000)
            },
        )
        watchEsRef.current = es
    }, [loadThreads])

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
            const res = await fetch(`/api/threads/${threadKey}/inject-context`, {
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
                ws.send(JSON.stringify({ type: "tree", root: localPath, tree, threadKey }))
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

            // Parse args JSON (all tools send their params as a JSON string in "args")
            let params: Record<string, string> = {}
            try { if (args) params = JSON.parse(args) } catch { return }

            // Strip surrounding quotes the model sometimes wraps around path values
            if (params.path) params.path = params.path.replace(/^["']+|["']+$/g, "").trim()

            try {
                let result: string

                if (type === "read_file") {
                    const content = await window.electronBridge!.readFile(localPath, params.path ?? "")
                    result = `[file: "${params.path}"]\n\`\`\`\n${content}\n\`\`\``
                    console.warn(`[ToolSocket] → file_content  callId=${callId}  bytes=${result.length}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "list_directory") {
                    const entries = await window.electronBridge!.listDirectory(localPath, params.path)
                    result = `[directory: "${params.path ?? "."}"]\n${entries.join("\n")}`
                    console.warn(`[ToolSocket] → file_content (list_directory)  callId=${callId}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "search_files") {
                    const matches = await window.electronBridge!.searchFiles(localPath, params.pattern, params.path, params.glob)
                    result = matches.length === 0
                        ? `No matches found for "${params.pattern}".`
                        : `[search: "${params.pattern}"]\n${matches.join("\n")}`
                    console.warn(`[ToolSocket] → file_content (search_files)  callId=${callId}  matches=${matches.length}`)
                    ws.send(JSON.stringify({ type: "file_content", callId, content: result }))

                } else if (type === "edit_file") {
                    if (safetyModeRef.current) {
                        const diff = buildSafetyDiff(params.old_string ?? "", params.new_string ?? "")
                        console.warn(`[ToolSocket] → file_content (edit_file BLOCKED by safety)  callId=${callId}`)
                        ws.send(JSON.stringify({ type: "file_error", callId, error: `SAFETY MODE — file was NOT modified. Do not call edit_file or write_file again. Respond to the user now: tell them safety mode is on, show the proposed changes as a code block, and say they can disable safety mode (shield icon) to apply them.\n\nProposed diff for ${params.path}:\n\n${diff}` }))
                    } else {
                        const res = await window.electronBridge!.editFile(localPath, params.path, params.old_string, params.new_string)
                        if (res.ok) {
                            console.warn(`[ToolSocket] → file_content (edit_file)  callId=${callId}  path=${params.path}`)
                            ws.send(JSON.stringify({ type: "file_content", callId, content: `Successfully edited ${params.path}.` }))
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
                        console.warn(`[ToolSocket] → file_content (write_file)  callId=${callId}  path=${params.path}`)
                        ws.send(JSON.stringify({ type: "file_content", callId, content: `Successfully wrote ${params.path}.` }))
                    }
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
        watchEsRef.current?.close(); watchEsRef.current = null
        watchRenderedRef.current = false

        setActiveThread(key)
        setIsInternal(internal)
        setAgentName(agName)
        setCodeMode(isCode)
        setIsStreaming(false)
        setIsRemembering(false)
        activeProjectRef.current = projectId
        setSelectedProject(projectId)

        const hist = await loadHistory(key, internal).catch(() => [])
        setItems(hist)
        activate(hist.length > 0)

        if (!internal) {
            openWatch(key, agName)
            await refreshThreadAttach(key)
            if (projectId) {
                await injectFileTree(key, projectId)
                await openToolSocket(key, projectId)
            }
        }
    }, [openWatch, refreshThreadAttach, injectFileTree, openToolSocket])

    // ── new chat ──────────────────────────────────────────
    function newChat() {
        abortRef.current?.abort(); abortRef.current = null
        watchEsRef.current?.close(); watchEsRef.current = null
        toolSocketRef.current?.close(); toolSocketRef.current = null; toolSocketKeyRef.current = null
        setActiveThread(null); setIsInternal(false); setAgentName(null)
        setCodeMode(false); setIsStreaming(false); setIsRemembering(false)
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
            key = await createThread(selectedProject)
            setActiveThread(key)
            activeProjectRef.current = selectedProject
            openWatch(key, null)
            if (selectedProject) {
                await injectFileTree(key, selectedProject)
            }
        } else if (mode === "idle") {
            activate()
        }

        // Always ensure tools are bound before sending — covers both new threads and
        // existing threads opened after a server restart (openToolSocket no-ops if already bound)
        const project = selectedProject ?? activeProjectRef.current
        if (project) {
            await openToolSocket(key!, project)
        }

        if (prompt.startsWith("/")) {
            try {
                const res = await fetch("/api/commands", {
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
            watchRenderedRef.current = false
        }

        const ctrl = new AbortController()
        abortRef.current = ctrl
        const keyForStream = key!

        async function runStream() {
            try {
                const resp = await fetch(`/api/threads/${keyForStream}/stream`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ prompt }),
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
                        loadHistory(keyForStream).then(hist => {
                            setItems(hist)
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
                    if (watchRenderedRef.current) return

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
    }, [isStreaming, pendingAttach, items.length, mode, openWatch, loadThreads, selectedProject, injectFileTree, openToolSocket])

    // ── upload thread attachment ──────────────────────────
    const uploadThreadFiles = useCallback(async (files: File[]) => {
        let key = activeThreadRef.current
        if (!key) { key = await createThread(); setActiveThread(key); openWatch(key, null); activate() }

        const succeeded: string[] = []
        for (const file of files) {
            const fd = new FormData(); fd.append("file", file)
            const res = await fetch(`/api/threads/${key}/attachments`, { method: "POST", body: fd })
            if (res.ok) succeeded.push(file.name)
            else { const err = await res.json().catch(() => null); showToast(err?.error ?? `Could not attach ${file.name}.`) }
        }
        if (succeeded.length) await refreshThreadAttach(key)
        return succeeded
    }, [openWatch, refreshThreadAttach, showToast])

    const removeThreadAttachment = useCallback(async (name: string) => {
        if (!activeThreadRef.current) return
        await fetch(`/api/threads/${activeThreadRef.current}/attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        await refreshThreadAttach(activeThreadRef.current)
    }, [refreshThreadAttach])

    // ── upload message attachment ─────────────────────────
    const uploadMessageFiles = useCallback(async (files: File[]) => {
        let key = activeThreadRef.current
        if (!key) { key = await createThread(); setActiveThread(key); openWatch(key, null) }

        const uploading = files.map(f => ({
            name: f.name, isImage: false, mimeType: null, content: null, uploading: true,
        }))
        setPendingAttach(prev => {
            const filtered = prev.filter(a => !files.some(f => f.name === a.name))
            return [...filtered, ...uploading]
        })

        for (const file of files) {
            const fd = new FormData(); fd.append("file", file)
            const res = await fetch(`/api/threads/${key}/message-attachments`, { method: "POST", body: fd })
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
    }, [openWatch, showToast])

    const removeMessageAttachment = useCallback(async (name: string) => {
        if (!activeThreadRef.current) return
        await fetch(`/api/threads/${activeThreadRef.current}/message-attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        setPendingAttach(prev => prev.filter(a => a.name !== name))
    }, [])

    const isWin32 = window.electronBridge?.platform === "win32"

    return (
        <div id="shell">
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
                />
            )}
            {toasts.map(t => (
                <div key={t.id} className="toast toast-visible">{t.msg}</div>
            ))}
        </div>
    )
}
