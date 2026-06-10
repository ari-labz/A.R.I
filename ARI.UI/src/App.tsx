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
    const [isTyping,         setIsTyping]          = useState(false)
    const [typingLabel,      setTypingLabel]        = useState("A·R·I is thinking")
    const [sidebarCollapsed, setSidebarCollapsed]  = useState(false)
    const [pendingAttach,    setPendingAttach]      = useState<PendingAttachment[]>([])
    const [threadAttach,     setThreadAttach]       = useState<Attachment[]>([])
    const [toasts,           setToasts]            = useState<{ id: string; msg: string }[]>([])
    const [activeView,       setActiveView]        = useState<"chat" | "projects">("chat")
    const [projects,         setProjects]          = useState<Project[]>([])
    const [selectedProject,  setSelectedProject]   = useState<string | null>(null)

    const watchEsRef  = useRef<EventSource | null>(null)
    const abortRef    = useRef<AbortController | null>(null)
    const pendingMsgRef    = useRef<string | null>(null)
    const preSendCountRef  = useRef(0)
    const watchRenderedRef = useRef(false)
    const activeThreadRef   = useRef<string | null>(null)
    const streamingRef      = useRef(false)
    const activeProjectRef  = useRef<string | null>(null)   // projectId of current thread
    const treeInjectedRef   = useRef<Set<string>>(new Set()) // threadKeys whose tree was already injected

    // Keep refs in sync
    useEffect(() => { activeThreadRef.current = activeThread }, [activeThread])
    useEffect(() => { streamingRef.current = isStreaming }, [isStreaming])

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
                    setIsTyping(false)
                    setMode("idle")
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
                    if (data.isRemembering && !data.isProcessing) {
                        setIsTyping(true); setTypingLabel("Remembering")
                    } else if (data.isProcessing) {
                        setIsTyping(true); setTypingLabel("A·R·I is thinking")
                    } else {
                        setIsTyping(false)
                    }
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
            if (tree.length > MAX_PATHS) {
                tree = tree.slice(0, MAX_PATHS)
                truncated = true
            }
            const header = `Project: ${project.name}\nRoot: ${localPath}\nFiles (${tree.length}${truncated ? "+" : ""}):\n`
            const content = header + tree.join("\n") + (truncated ? "\n... (truncated)" : "")
            console.log(`[FileTree] Injecting ${tree.length} paths for thread ${threadKey}`)
            const res = await fetch(`/api/threads/${threadKey}/inject-context`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: "_project_tree.txt", content }),
            })
            if (res.ok) {
                treeInjectedRef.current.add(threadKey)
                console.log(`[FileTree] Injected successfully`)
            } else {
                console.warn(`[FileTree] inject-context returned ${res.status}`)
            }
        } catch (e) { console.error("[FileTree] Error:", e) }
    }, [projects])

    const handleFileToolCalls = useCallback(async (threadKey: string, responseText: string) => {
        if (!window.electronBridge) return
        const projectId = activeProjectRef.current
        if (!projectId) return
        const localPath = await env.getLocalPath(projectId)
        if (!localPath) return

        const pattern = /\[read_file:\s*"([^"]+)"\]/g
        const matches = [...responseText.matchAll(pattern)]
        if (!matches.length) return

        const parts: string[] = []
        for (const match of matches) {
            const relPath = match[1]
            try {
                const content = await window.electronBridge.readFile(localPath, relPath)
                parts.push(`[file: "${relPath}"]\n\`\`\`\n${content}\n\`\`\``)
            } catch {
                parts.push(`[file: "${relPath}"]\n(File not found or unreadable)`)
            }
        }
        if (parts.length) {
            // Slight delay so the response fully renders before the follow-up arrives
            await new Promise(r => setTimeout(r, 300))
            // Send file contents as a continuation — use the existing send path
            const payload = parts.join("\n\n")
            await fetch(`/api/threads/${threadKey}/stream`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ prompt: payload }),
            })
        }
    }, [projects])

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
        setIsTyping(false)
        activeProjectRef.current = projectId

        const hist = await loadHistory(key, internal).catch(() => [])
        setItems(hist)
        activate(hist.length > 0)

        if (!internal) {
            openWatch(key, agName)
            await refreshThreadAttach(key)
            if (isCode && projectId) injectFileTree(key, projectId)
        }
    }, [openWatch, refreshThreadAttach, injectFileTree])

    // ── new chat ──────────────────────────────────────────
    function newChat() {
        abortRef.current?.abort(); abortRef.current = null
        watchEsRef.current?.close(); watchEsRef.current = null
        setActiveThread(null); setIsInternal(false); setAgentName(null)
        setCodeMode(false); setIsStreaming(false); setIsTyping(false)
        setPendingAttach([]); setThreadAttach([])
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
            setIsStreaming(false); setIsTyping(false)
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
            setItems(prev => [...prev, userItem])
            setIsStreaming(true)
            setIsTyping(true); setTypingLabel("A·R·I is thinking")
        }

        let key = activeThreadRef.current
        if (needsNew) {
            key = await createThread(selectedProject)
            setActiveThread(key)
            activeProjectRef.current = selectedProject
            openWatch(key, null)
            // Await the injection so the file tree is in pendingAttachments before the stream flushes them
            if (selectedProject) await injectFileTree(key, selectedProject)
        } else if (mode === "idle") {
            activate()
        }

        if (prompt.startsWith("/")) {
            setIsTyping(true)
            try {
                const res = await fetch("/api/commands", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ threadKey: key, input: prompt }),
                })
                if (!res.ok) {
                    const data = await res.json()
                    setIsTyping(false)
                    setItems(prev => [...prev, {
                        type: "commandExchange", input: prompt,
                        response: data.error ?? "Command failed.",
                        timestamp: new Date().toISOString(), content: "",
                    }])
                }
            } catch {
                setIsTyping(false)
                setItems(prev => [...prev, {
                    type: "commandExchange", input: prompt,
                    response: "Command failed — could not reach A·R·I.",
                    timestamp: new Date().toISOString(), content: "",
                }])
            }
            return
        }

        if (!needsNew) {
            const optimisticAttach = pendingAttach.length ? [...pendingAttach] : undefined
            setPendingAttach([])
            pendingMsgRef.current = prompt
            preSendCountRef.current = items.length
            setItems(prev => [...prev, {
                type: "userMessage", content: prompt,
                timestamp: new Date().toISOString(),
                attachments: optimisticAttach as Attachment[] | undefined,
            }])
            setIsStreaming(true)
            setIsTyping(true); setTypingLabel("A·R·I is thinking")
        }

        const ctrl = new AbortController()
        abortRef.current = ctrl
        const keyForStream = key!

        let streamingItemAdded = false

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
                        setIsStreaming(false); setIsTyping(false)
                        pendingMsgRef.current = null
                        loadThreads()
                        // Refresh with full metadata, then scan for file tool calls
                        loadHistory(keyForStream).then(hist => {
                            setItems(hist)
                            const lastResponse = [...hist].reverse().find(i => i.type === "ariResponse")
                            if (lastResponse) handleFileToolCalls(keyForStream, lastResponse.content)
                        }).catch(() => {})
                        return
                    }
                    if (data === "[CANCELLED]") {
                        abortRef.current = null
                        setIsStreaming(false); setIsTyping(false)
                        setItems(prev => prev.slice(0, preSendCountRef.current))
                        pendingMsgRef.current = null
                        return
                    }
                    if (data.startsWith("[ERROR]")) {
                        setIsTyping(false)
                        setItems(prev => [...prev, {
                            type: "ariResponse", content: data.replace("[ERROR] ", ""),
                            timestamp: new Date().toISOString(),
                        }])
                        abortRef.current = null; setIsStreaming(false)
                        return
                    }
                    if (watchRenderedRef.current) return

                    const text = data.replace(/\\n/g, "\n")
                    if (!streamingItemAdded) {
                        streamingItemAdded = true
                        setItems(prev => [...prev, {
                            type: "ariResponse", content: text,
                            timestamp: new Date().toISOString(),
                        }])
                    } else {
                        // Each SSE event carries the full accumulated response — replace, don't append
                        setItems(prev => {
                            const last = prev[prev.length - 1]
                            if (!last || last.type !== "ariResponse") return prev
                            return [...prev.slice(0, -1), { ...last, content: text }]
                        })
                    }
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
                setIsTyping(false)
                setItems(prev => [...prev, {
                    type: "ariResponse", content: "[connection error]",
                    timestamp: new Date().toISOString(),
                }])
                abortRef.current = null; setIsStreaming(false)
            }
        }

        runStream()
    }, [isStreaming, pendingAttach, items.length, mode, openWatch, loadThreads, selectedProject, injectFileTree, handleFileToolCalls])

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

    return (
        <div id="shell">
            <Sidebar
                threads={threads}
                activeThread={activeThread}
                activeView={activeView}
                onNewChat={newChat}
                onOpenProjects={() => setActiveView("projects")}
                onSelectThread={(t: ThreadEntry) => { setActiveView("chat"); openThread(t.key, t.isInternal, t.agentName, t.isCodeMode, t.projectId ?? null) }}
                collapsed={sidebarCollapsed}
                onToggleCollapse={() => setSidebarCollapsed(c => !c)}
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
                    isTyping={isTyping}
                    typingLabel={typingLabel}
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
