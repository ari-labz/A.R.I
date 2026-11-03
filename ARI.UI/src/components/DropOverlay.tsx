interface Props {
    visible:       boolean
    hovering:      boolean
    onDropMessage: (files: File[]) => void
}

// Dropped files attach to the next message (and land in the thread's workspace with it). A dropped .ari plugin is
// picked out by the handler and installed instead.
export default function DropOverlay({ visible, hovering, onDropMessage }: Props) {
    if (!visible) return null

    function handleDrop(e: React.DragEvent) {
        e.preventDefault(); e.stopPropagation()
        onDropMessage([...(e.dataTransfer.files ?? [])])
    }

    return (
        <div id="drop-overlay" style={{ display: "flex" }}>
            <div
                id="drop-zone-message"
                className={`drop-zone${hovering ? " drag-over" : ""}`}
                onDragOver={e => e.preventDefault()}
                onDrop={handleDrop}
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
