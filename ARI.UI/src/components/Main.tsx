import { useState, useRef, useCallback, useEffect } from "react"
import type { PipelineOption } from "../hooks/usePipelines"
import Messages from "./Messages"
import InputArea from "./InputArea"
import ThreadPanel from "./ThreadPanel"
import DropOverlay from "./DropOverlay"
import BrowserPane, { BrowserButton, BrowserNeedsYou } from "./BrowserPane"
import { useBrowserSession } from "../hooks/useBrowserSession"
import Orb from "./Orb"
import type { AppMode, PendingAttachment } from "../App"
import type { ThreadItem, Attachment, Project } from "../hooks/useThreads"
import { apiFetch } from "../auth"

interface Command { cmd: string; desc: string }

interface Props {
    mode:          AppMode
    codeMode:      boolean
    shieldMode:    boolean
    planProposed:  boolean
    items:         ThreadItem[]
    isRemembering: boolean
    isStreaming:   boolean
    threadStatus?: "idle" | "prefilling" | "thinking" | "typing" | "queued" | "remembering" | "researching" | "syncing" | "generating" | "waiting" | "working"
    activeThread:  string | null
    isInternal:    boolean
    agentName:     string | null
    sidebarCollapsed: boolean
    onOpenSidebar: () => void
    pendingAttach: PendingAttachment[]
    threadAttach:  Attachment[]
    onSend:        (text: string) => void
    onUploadThreadFiles:  (files: File[]) => Promise<string[]>
    onUploadMessageFiles: (files: File[]) => void
    onRemoveThreadAttach: (name: string) => void
    onRemoveMessageAttach: (name: string) => void
    onHeartbeatStart:  () => void
    onHeartbeatStop:   () => void
    safetyMode:        boolean
    onToggleSafety:    () => void
    commands:         Command[]
    projects:         Project[]
    selectedProject:  string | null
    onProjectChange:  (id: string | null) => void
    pipelines:        PipelineOption[]
    selectedPipeline: string | null
    onPipelineChange: (id: string | null) => void
    speechMode:       boolean
    onBeginSpeech:    () => void
    speechCaption:    string | null
    speechOrbState:   "listening" | "thinking" | "speaking"
    serverReady:      boolean
}

export default function Main({
    mode, codeMode, shieldMode, planProposed, items, isRemembering, isStreaming, threadStatus,
    activeThread, isInternal, agentName,
    sidebarCollapsed, onOpenSidebar,
    pendingAttach, threadAttach,
    onSend, onUploadThreadFiles, onUploadMessageFiles,
    onRemoveThreadAttach, onRemoveMessageAttach,
    onHeartbeatStart, onHeartbeatStop, commands,
    projects, selectedProject, onProjectChange,
    pipelines, selectedPipeline, onPipelineChange,
    speechMode, onBeginSpeech, speechCaption, speechOrbState,
    safetyMode, onToggleSafety, serverReady,
}: Props) {
    const [threadPanelOpen, setThreadPanelOpen] = useState(false)
    const [dropVisible,     setDropVisible]     = useState(false)
    const [dropHovering,    setDropHovering]    = useState<"thread" | "message" | null>(null)
    const dragCounterRef = useRef(0)
    const threadFileRef  = useRef<HTMLInputElement>(null)

    // Close thread panel when thread changes
    useEffect(() => { setThreadPanelOpen(false) }, [activeThread])

    // Drag-drop on document
    const handleDragEnter = useCallback((e: DragEvent) => {
        if (!e.dataTransfer?.types?.includes("Files")) return
        e.preventDefault()
        dragCounterRef.current++
        setDropVisible(true)
    }, [])
    const handleDragLeave = useCallback(() => {
        dragCounterRef.current--
        if (dragCounterRef.current <= 0) {
            dragCounterRef.current = 0
            setDropVisible(false); setDropHovering(null)
        }
    }, [])
    const handleDragOver = useCallback((e: DragEvent) => {
        if (e.dataTransfer?.types?.includes("Files")) e.preventDefault()
    }, [])
    const handleDrop = useCallback((e: DragEvent) => {
        e.preventDefault()
        dragCounterRef.current = 0
        setDropVisible(false); setDropHovering(null)
    }, [])

    useEffect(() => {
        document.addEventListener("dragenter", handleDragEnter)
        document.addEventListener("dragleave", handleDragLeave)
        document.addEventListener("dragover",  handleDragOver)
        document.addEventListener("drop",      handleDrop)
        return () => {
            document.removeEventListener("dragenter", handleDragEnter)
            document.removeEventListener("dragleave", handleDragLeave)
            document.removeEventListener("dragover",  handleDragOver)
            document.removeEventListener("drop",      handleDrop)
        }
    }, [handleDragEnter, handleDragLeave, handleDragOver, handleDrop])

    // Escape = stop. Cancels the in-flight turn but preserves the partial work (thinking + partial reply);
    // it is not thrown away, so the transcript keeps what she had so far and the next message is a fresh turn.
    useEffect(() => {
        function onKey(e: KeyboardEvent) {
            if (e.key !== "Escape" || !isStreaming || !activeThread) return
            apiFetch(`/api/v1/threads/${activeThread}/interrupt`, { method: "POST" }).catch(() => {})
        }
        document.addEventListener("keydown", onKey)
        return () => document.removeEventListener("keydown", onKey)
    }, [isStreaming, activeThread])

    // An .ari file dropped anywhere is a plugin to install, not an attachment. The server only lets the owner install; anyone else gets its refusal.
    function takePluginFiles(files: File[]): boolean {
        const pkg = files.find(f => /\.ari$/i.test(f.name))
        if (!pkg) return false
        if (!window.confirm(`Install ${pkg.name}?\n\nA plugin is code that runs on this machine with full access to ARI. Only install ones you trust. It arrives switched off; turn it on from the Plugins page.`)) return true
        apiFetch("/api/v1/admin/plugins/install", { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: pkg })
            .then(async r => {
                const j = await r.json().catch(() => ({} as { error?: string; id?: string }))
                window.alert(r.ok ? `Installed. It's switched off: open Plugins in the control panel to turn on ${j.id}.` : (j.error || "The install failed."))
            })
            .catch(() => window.alert("The install failed."))
        return true
    }

    async function handleDropThread(files: File[]) {
        dragCounterRef.current = 0
        setDropVisible(false); setDropHovering(null)
        if (!files.length || takePluginFiles(files)) return
        const succeeded = await onUploadThreadFiles(files)
        if (succeeded.length) {
            setThreadPanelOpen(false)
            // bounce animation
            const panel = document.getElementById("thread-panel")
            if (panel) {
                panel.classList.remove("panel-peek")
                void panel.offsetWidth
                panel.classList.add("panel-peek")
                panel.addEventListener("animationend", () => panel.classList.remove("panel-peek"), { once: true })
            }
        }
    }

    function handleDropMessage(files: File[]) {
        dragCounterRef.current = 0
        setDropVisible(false); setDropHovering(null)
        if (files.length && !takePluginFiles(files)) onUploadMessageFiles(files)
    }

    const browser = useBrowserSession(activeThread)

    const mainClasses = [
        mode === "active" ? "active" : "",
        codeMode ? "code-mode" : "",
        speechMode ? "speech-mode" : "",
    ].filter(Boolean).join(" ")

    return (
        <div id="main" className={mainClasses}>
            {/* floating toggle when sidebar collapsed */}
            <button
                id="topbar-toggle"
                className={sidebarCollapsed ? "visible" : ""}
                title="Open sidebar"
                onClick={onOpenSidebar}
            >
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/>
                </svg>
            </button>

            <div id="chat-col">
                {/* animated logo header */}
                <div id="main-header">
                    <div id="header-logos">
                        <img id="main-logo" src="/images/logo-black.png?v=2" alt="A·R·I" />
                        <img id="code-logo" src="/images/BlackCode.png?v=2" alt="Code" />
                    </div>
                    <BrowserButton session={browser} />
                </div>

                {/* messages + thread panel zone */}
                <div id="messages-wrap">
                    <DropOverlay
                        visible={dropVisible}
                        hovering={dropHovering}
                        onDropThread={handleDropThread}
                        onDropMessage={handleDropMessage}
                    />

                    <button
                        id="btn-open-thread-panel"
                        className={threadPanelOpen ? "hidden" : ""}
                        title="Open thread panel"
                        onClick={() => setThreadPanelOpen(true)}
                    >
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <rect x="3" y="3" width="18" height="18" rx="2"/><path d="M15 3v18"/>
                        </svg>
                    </button>

                    {speechMode && activeThread ? (
                        <Orb state={speechOrbState} caption={speechCaption} />
                    ) : (
                        <Messages
                            items={items}
                            isRemembering={isRemembering}
                            activeThread={activeThread}
                            isInternal={isInternal}
                            agentName={agentName}
                            processing={isStreaming}
                            threadStatus={threadStatus}
                        />
                    )}

                    <ThreadPanel
                        open={threadPanelOpen}
                        attachments={threadAttach}
                        activeThread={activeThread}
                        onClose={() => setThreadPanelOpen(false)}
                        onAttach={() => threadFileRef.current?.click()}
                        onRemove={onRemoveThreadAttach}
                    />
                    <input
                        ref={threadFileRef} type="file" multiple
                        style={{ display: "none" }}
                        onChange={e => {
                            if (e.target.files?.length) onUploadThreadFiles([...e.target.files])
                            e.target.value = ""
                        }}
                    />
                </div>

                <BrowserNeedsYou session={browser} />

                {!isInternal && !(speechMode && activeThread) && (
                    <InputArea
                        isStreaming={isStreaming}
                        planProposed={planProposed}
                        pendingAttach={pendingAttach}
                        commands={commands}
                        projects={projects}
                        selectedProject={selectedProject}
                        onProjectChange={onProjectChange}
                        pipelines={pipelines}
                        selectedPipeline={selectedPipeline}
                        onPipelineChange={onPipelineChange}
                        onBeginSpeech={onBeginSpeech}
                        threadLocked={activeThread !== null}
                        serverReady={serverReady}
                        onSend={onSend}
                        onUploadFiles={onUploadMessageFiles}
                        onRemoveAttach={onRemoveMessageAttach}
                        onHeartbeatStart={onHeartbeatStart}
                        onHeartbeatStop={onHeartbeatStop}
                        codeMode={shieldMode}
                        safetyMode={safetyMode}
                        onToggleSafety={onToggleSafety}
                    />
                )}
            </div>

            <BrowserPane session={browser} />
        </div>
    )
}
