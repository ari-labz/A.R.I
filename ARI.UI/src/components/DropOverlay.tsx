interface Props {
    visible:      boolean
    hovering:     "thread" | "message" | null
    onDropThread: (files: File[]) => void
    onDropMessage: (files: File[]) => void
}

export default function DropOverlay({ visible, hovering, onDropThread, onDropMessage }: Props) {
    if (!visible) return null

    function handleDrop(e: React.DragEvent, zone: "thread" | "message") {
        e.preventDefault(); e.stopPropagation()
        const files = [...(e.dataTransfer.files ?? [])]
        if (zone === "thread")   onDropThread(files)
        else                     onDropMessage(files)
    }

    return (
        <div id="drop-overlay" style={{ display: "flex" }}>
            <div
                id="drop-zone-thread"
                className={`drop-zone${hovering === "thread" ? " drag-over" : ""}`}
                onDragOver={e => e.preventDefault()}
                onDrop={e => handleDrop(e, "thread")}
            >
                <svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
                    <line x1="12" y1="17" x2="12" y2="3"/><polyline points="8 7 12 3 16 7"/>
                    <path d="M20 21H4"/><line x1="17" y1="14" x2="17" y2="21"/><line x1="7" y1="14" x2="7" y2="21"/>
                </svg>
                <p className="drop-zone-title">Pin to thread</p>
                <p className="drop-zone-sub">Always in context</p>
            </div>
            <div
                id="drop-zone-message"
                className={`drop-zone${hovering === "message" ? " drag-over" : ""}`}
                onDragOver={e => e.preventDefault()}
                onDrop={e => handleDrop(e, "message")}
            >
                <svg width="36" height="36" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>
                </svg>
                <p className="drop-zone-title">Attach to message</p>
                <p className="drop-zone-sub">Sent with your next message</p>
            </div>
        </div>
    )
}
