import { useRef, useEffect, useState, type KeyboardEvent } from "react"
import type { PendingAttachment } from "../App"
import type { Project } from "../hooks/useThreads"

interface Command { cmd: string; desc: string }

interface Props {
    isStreaming:      boolean
    pendingAttach:    PendingAttachment[]
    commands:         Command[]
    projects:         Project[]
    selectedProject:  string | null
    onProjectChange:  (id: string | null) => void
    threadLocked:     boolean
    onSend:           (text: string) => void
    onUploadFiles:    (files: File[]) => void
    onRemoveAttach:   (name: string) => void
    onHeartbeatStart: () => void
    onHeartbeatStop:  () => void
    codeMode:         boolean
    safetyMode:       boolean
    onToggleSafety:   () => void
}

function fileExtLabel(name: string) {
    const dot = name.lastIndexOf(".")
    return dot >= 0 ? name.slice(dot + 1).toUpperCase().slice(0, 4) : "FILE"
}

export default function InputArea({
    isStreaming, pendingAttach, commands,
    projects, selectedProject, onProjectChange, threadLocked,
    onSend, onUploadFiles, onRemoveAttach,
    onHeartbeatStart, onHeartbeatStop,
    codeMode, safetyMode, onToggleSafety,
}: Props) {
    const [input, setInput]         = useState("")
    const [cmdMatches, setCmdMatches] = useState<Command[]>([])
    const [cmdIndex, setCmdIndex]   = useState(-1)
    const textareaRef = useRef<HTMLTextAreaElement>(null)
    const fileInputRef = useRef<HTMLInputElement>(null)
    const wrapRef     = useRef<HTMLDivElement>(null)

    // Auto-resize textarea
    useEffect(() => {
        const ta = textareaRef.current
        if (!ta) return
        ta.style.height = "auto"
        ta.style.height = `${Math.min(ta.scrollHeight, 180)}px`
    }, [input])

    function updateCmdPopup(val: string) {
        if (!val.startsWith("/")) { setCmdMatches([]); setCmdIndex(-1); return }
        const query = val.toLowerCase()
        const matches = commands.filter(c => c.cmd.toLowerCase().startsWith(query))
        if (!matches.length || matches.some(c => c.cmd.toLowerCase() === query.trimEnd())) {
            setCmdMatches([]); setCmdIndex(-1); return
        }
        setCmdMatches(matches)
    }

    function applyCmd(c: Command) {
        setInput(c.cmd + " ")
        setCmdMatches([]); setCmdIndex(-1)
        textareaRef.current?.focus()
    }

    function handleKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
        if (cmdMatches.length > 0) {
            if (e.key === "ArrowDown") { e.preventDefault(); setCmdIndex(i => (i + 1) % cmdMatches.length); return }
            if (e.key === "ArrowUp")   { e.preventDefault(); setCmdIndex(i => (i - 1 + cmdMatches.length) % cmdMatches.length); return }
            if (e.key === "Tab")       { e.preventDefault(); applyCmd(cmdMatches[cmdIndex >= 0 ? cmdIndex : 0]); return }
            if (e.key === "Escape")    { setCmdMatches([]); setCmdIndex(-1); return }
        }
        const isMobile = window.matchMedia("(pointer: coarse)").matches
        if (e.key === "Enter" && !e.shiftKey && !isMobile) {
            e.preventDefault(); submit()
        }
    }

    function submit() {
        const text = input.trim()
        if (!text && pendingAttach.length === 0) return
        onSend(text)
        setInput("")
        setCmdMatches([]); setCmdIndex(-1)
    }

    async function handlePaste(e: React.ClipboardEvent<HTMLTextAreaElement>) {
        const items = [...(e.clipboardData?.items ?? [])]
        const imageItems = items.filter(i => i.kind === "file" && i.type.startsWith("image/"))
        if (imageItems.length) {
            e.preventDefault()
            const files = imageItems.map(i => i.getAsFile()).filter(Boolean) as File[]
            if (files.length) onUploadFiles(files)
            return
        }
        const text = e.clipboardData?.getData("text/plain") ?? ""
        if (text.length > 1000) {
            e.preventDefault()
            const blob = new Blob([text], { type: "text/plain" })
            const file = new File([blob], "paste.txt", { type: "text/plain" })
            onUploadFiles([file])
        }
    }

    return (
        <div id="input-area">
            <div id="input-wrap" ref={wrapRef} onClick={e => {
                const tag = (e.target as HTMLElement).tagName
                if (tag !== "SELECT" && tag !== "BUTTON" && tag !== "INPUT") textareaRef.current?.focus()
            }}>
                {codeMode && (
                    <button
                        id="btn-safety-toggle"
                        className={safetyMode ? "active" : ""}
                        title={safetyMode ? "Safety on — ARI will not modify files" : "Safety off — ARI can modify files"}
                        onClick={e => { e.stopPropagation(); onToggleSafety() }}
                    >
                        <svg width="14" height="14" viewBox="0 0 24 24" fill={safetyMode ? "currentColor" : "none"} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
                        </svg>
                    </button>
                )}
                {/* pre-send attachment chips */}
                <div id="msg-attach-preview">
                    {pendingAttach.map(a => (
                        <div key={a.name} className={`msg-attach-chip${a.uploading ? " uploading" : ""}`}>
                            {a.uploading
                                ? <div className="chip-file-icon chip-uploading-icon">…</div>
                                : a.isImage && a.content
                                    ? <img src={`data:${a.mimeType};base64,${a.content}`} alt={a.name} />
                                    : <div className="chip-file-icon">{fileExtLabel(a.name)}</div>}
                            <span className="chip-name" title={a.name}>{a.uploading ? "Uploading…" : a.name}</span>
                            {!a.uploading && <button className="chip-remove" onClick={e => { e.stopPropagation(); onRemoveAttach(a.name) }}>×</button>}
                        </div>
                    ))}
                </div>

                {/* slash command popup */}
                {cmdMatches.length > 0 && (
                    <div id="cmd-popup">
                        <ul id="cmd-list">
                            {cmdMatches.map((c, i) => (
                                <li key={c.cmd} className={i === cmdIndex ? "active" : ""}
                                    onMouseDown={e => { e.preventDefault(); applyCmd(c) }}>
                                    <span className="cmd-name">{c.cmd}</span>
                                    <span className="cmd-desc">{c.desc}</span>
                                </li>
                            ))}
                        </ul>
                    </div>
                )}

                <textarea
                    id="input"
                    ref={textareaRef}
                    rows={1}
                    value={input}
                    placeholder="Message A·R·I..."
                    onChange={e => { setInput(e.target.value); updateCmdPopup(e.target.value) }}
                    onKeyDown={handleKeyDown}
                    onPaste={handlePaste}
                    onFocus={() => { if (input.trim()) onHeartbeatStart() }}
                    onBlur={onHeartbeatStop}
                    onInput={() => { if (input.trim()) onHeartbeatStart(); else onHeartbeatStop() }}
                />
                <div id="input-footer">
                    <div id="input-project-row">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                        </svg>
                        <select
                            id="input-project-select"
                            value={selectedProject ?? ""}
                            onChange={e => onProjectChange(e.target.value || null)}
                            disabled={threadLocked}
                        >
                            <option value="">No project</option>
                            {projects.map(p => (
                                <option key={p.id} value={p.id}>{p.name}</option>
                            ))}
                        </select>
                    </div>
                    <div id="input-actions">
                        <button id="btn-attach" title="Attach file" onClick={() => fileInputRef.current?.click()}>
                            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>
                            </svg>
                        </button>
                        <input
                            ref={fileInputRef} type="file" multiple
                            style={{ display: "none" }}
                            onChange={e => {
                                if (e.target.files?.length) onUploadFiles([...e.target.files])
                                e.target.value = ""
                            }}
                        />
                        <button
                            className="btn-send"
                            disabled={isStreaming || (!input.trim() && pendingAttach.length === 0)}
                            onClick={submit}
                            title="Send"
                        >
                            <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor">
                                <path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/>
                            </svg>
                        </button>
                    </div>
                </div>
            </div>
            <p id="input-hint">Enter to send &nbsp;·&nbsp; Shift+Enter for new line</p>
        </div>
    )
}
