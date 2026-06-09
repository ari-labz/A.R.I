import { useState, useRef, useCallback, useEffect } from "react"
import Messages from "./Messages"
import InputArea from "./InputArea"
import ThreadPanel from "./ThreadPanel"
import DropOverlay from "./DropOverlay"
import type { AppMode, PendingAttachment } from "../App"
import type { ThreadItem, Attachment } from "../hooks/useThreads"

interface Command { cmd: string; desc: string }

interface Props {
    mode:          AppMode
    codeMode:      boolean
    items:         ThreadItem[]
    isTyping:      boolean
    typingLabel:   string
    isStreaming:   boolean
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
    onHeartbeatStart: () => void
    onHeartbeatStop:  () => void
    commands:      Command[]
}

export default function Main({
    mode, codeMode, items, isTyping, typingLabel, isStreaming,
    activeThread, isInternal, agentName,
    sidebarCollapsed, onOpenSidebar,
    pendingAttach, threadAttach,
    onSend, onUploadThreadFiles, onUploadMessageFiles,
    onRemoveThreadAttach, onRemoveMessageAttach,
    onHeartbeatStart, onHeartbeatStop, commands,
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

    // Escape to cancel
    useEffect(() => {
        function onKey(e: KeyboardEvent) {
            if (e.key !== "Escape" || !isStreaming || !activeThread) return
            fetch(`/api/threads/${activeThread}/processing`, { method: "DELETE" }).catch(() => {})
        }
        document.addEventListener("keydown", onKey)
        return () => document.removeEventListener("keydown", onKey)
    }, [isStreaming, activeThread])

    async function handleDropThread(files: File[]) {
        dragCounterRef.current = 0
        setDropVisible(false); setDropHovering(null)
        if (!files.length) return
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
        if (files.length) onUploadMessageFiles(files)
    }

    function handleTitlebarDrag(e: React.MouseEvent) {
        if (!window.electronBridge) return
        e.preventDefault()
        let lastX = e.screenX
        let lastY = e.screenY

        function onMove(ev: MouseEvent) {
            const dx = ev.screenX - lastX
            const dy = ev.screenY - lastY
            lastX = ev.screenX
            lastY = ev.screenY
            window.electronBridge!.moveWindowBy(dx, dy)
        }
        function onUp() {
            document.removeEventListener("mousemove", onMove)
            document.removeEventListener("mouseup", onUp)
        }
        document.addEventListener("mousemove", onMove)
        document.addEventListener("mouseup", onUp)
    }

    const mainClasses = [
        mode === "active" ? "active" : "",
        codeMode ? "code-mode" : "",
    ].filter(Boolean).join(" ")

    return (
        <div id="main" className={mainClasses}>
            <div id="titlebar-drag" onMouseDown={handleTitlebarDrag} />
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
                        <img id="main-logo" src="/images/logo-black.png" alt="A·R·I" />
                        <img id="code-logo" src="/images/BlackCode.png" alt="Code" />
                    </div>
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

                    <Messages
                        items={items}
                        isTyping={isTyping}
                        typingLabel={typingLabel}
                        isStreaming={isStreaming}
                        activeThread={activeThread}
                        isInternal={isInternal}
                        agentName={agentName}
                    />

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

                {!isInternal && (
                    <InputArea
                        isStreaming={isStreaming}
                        pendingAttach={pendingAttach}
                        commands={commands}
                        onSend={onSend}
                        onUploadFiles={onUploadMessageFiles}
                        onRemoveAttach={onRemoveMessageAttach}
                        onHeartbeatStart={onHeartbeatStart}
                        onHeartbeatStop={onHeartbeatStop}
                    />
                )}
            </div>
        </div>
    )
}
