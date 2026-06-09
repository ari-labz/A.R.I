import { useState, useEffect, useCallback, useRef } from "react"
import Sidebar from "./components/Sidebar"
import Main from "./components/Main"
import {
    useThreads, createThread, loadHistory, openWatchStream, cancelProcessing,
    useTypingHeartbeat,
    type ThreadItem, type ThreadEntry, type WatchEvent, type Attachment,
} from "./hooks/useThreads"
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

    const watchEsRef  = useRef<EventSource | null>(null)
    const abortRef    = useRef<AbortController | null>(null)
    const pendingMsgRef    = useRef<string | null>(null)
    const preSendCountRef  = useRef(0)
    const watchRenderedRef = useRef(false)
    const activeThreadRef  = useRef<string | null>(null)
    const streamingRef     = useRef(false)

    // Keep refs in sync
    useEffect(() => { activeThreadRef.current = activeThread }, [activeThread])
    useEffect(() => { streamingRef.current = isStreaming }, [isStreaming])

    const heartbeat = useTypingHeartbeat(() => activeThreadRef.current)

    // ── init ─────────────────────────────────────────────
    useEffect(() => {
        async function waitAndInit() {
            while (true) {
                const res = await fetch("/api/threads").catch(() => null)
                if (res && res.status !== 503) break
                await new Promise(r => setTimeout(r, 2000))
            }
            await loadThreads()
        }
        waitAndInit()
        const pollId = setInterval(loadThreads, 5000)
        return () => clearInterval(pollId)
    }, [loadThreads])

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
    const openThread = useCallback(async (
        key: string, internal = false, agName: string | null = null, isCode = false,
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

        const hist = await loadHistory(key, internal).catch(() => [])
        setItems(hist)
        activate(hist.length > 0)

        if (!internal) {
            openWatch(key, agName)
            await refreshThreadAttach(key)
        }
    }, [openWatch, refreshThreadAttach])

    // ── new chat ──────────────────────────────────────────
    function newChat() {
        abortRef.current?.abort(); abortRef.current = null
        watchEsRef.current?.close(); watchEsRef.current = null
        setActiveThread(null); setIsInternal(false); setAgentName(null)
        setCodeMode(false); setIsStreaming(false); setIsTyping(false)
        setPendingAttach([]); setThreadAttach([])
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
            key = await createThread()
            setActiveThread(key)
            openWatch(key, null)
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
                        // Refresh with full metadata from server
                        loadHistory(keyForStream).then(hist => setItems(hist)).catch(() => {})
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

                    setIsTyping(false)
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
    }, [isStreaming, pendingAttach, items.length, mode, openWatch, loadThreads])

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
                onNewChat={newChat}
                onSelectThread={(t: ThreadEntry) => openThread(t.key, t.isInternal, t.agentName, t.isCodeMode)}
                collapsed={sidebarCollapsed}
                onToggleCollapse={() => setSidebarCollapsed(c => !c)}
            />
            <div id="sidebar-overlay"
                className={sidebarCollapsed ? "" : ""}
                onClick={() => setSidebarCollapsed(true)}
            />
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
            />
            {toasts.map(t => (
                <div key={t.id} className="toast toast-visible">{t.msg}</div>
            ))}
        </div>
    )
}
