import { useState } from "react"
import type { Attachment } from "../hooks/useThreads"

interface Props {
    open:         boolean
    attachments:  Attachment[]
    activeThread: string | null
    onClose:      () => void
    onAttach:     () => void
    onRemove:     (name: string) => void
}

export default function ThreadPanel({ open, attachments, activeThread, onClose, onAttach, onRemove }: Props) {
    const [lightbox, setLightbox] = useState<string | null>(null)

    function downloadLog() {
        if (!activeThread) return
        const a = document.createElement("a")
        a.href = `/threads/${activeThread}/export`
        a.click()
    }

    return (
        <>
        <aside id="thread-panel" className={open ? "open" : ""}>
            <div id="thread-panel-inner">
                <div id="thread-panel-header">
                    <span>Thread</span>
                    <button id="btn-close-thread-panel" onClick={onClose}>
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                            <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>
                        </svg>
                    </button>
                </div>
                <div id="thread-panel-actions">
                    <button id="btn-download-log" title="Download chat log" onClick={downloadLog}>
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
                            <polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/>
                        </svg>
                    </button>
                </div>
                <div className="thread-panel-section">
                    <div className="thread-panel-section-label">
                        Files
                        <button onClick={onAttach} title="Add file">
                            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>
                            </svg>
                        </button>
                    </div>
                    {attachments.length === 0
                        ? <div className="attachment-empty">No attachments</div>
                        : <>
                            {/* Image grid — thumbnails for generated/attached images */}
                            {attachments.some(a => a.isImage && a.url) && (
                                <div className="attachment-image-grid">
                                    {attachments.filter(a => a.isImage && a.url).map(a => (
                                        <div key={a.name} className="attachment-thumb-wrap" title={a.name} onClick={() => setLightbox(a.url!)}>
                                            <img src={a.url} alt={a.name} className="attachment-thumb" />
                                        </div>
                                    ))}
                                </div>
                            )}
                            {/* File list — non-image files */}
                            {attachments.some(a => !a.isImage) && (
                                <ul id="attachment-list">
                                    {attachments.filter(a => !a.isImage).map(a => (
                                        <li key={a.name}>
                                            <span className="attachment-item-name" title={a.name}>{a.name}</span>
                                            <button className="attachment-item-remove" onClick={() => onRemove(a.name)}>×</button>
                                        </li>
                                    ))}
                                </ul>
                            )}
                          </>
                    }
                </div>
            </div>
        </aside>
        {lightbox && (
            <div className="attachment-lightbox" onClick={() => setLightbox(null)}>
                <img src={lightbox} alt="Full size" />
            </div>
        )}
        </>
    )
}
