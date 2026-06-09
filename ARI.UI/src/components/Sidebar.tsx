import type { ThreadEntry } from "../hooks/useThreads"

interface Props {
    threads:         ThreadEntry[]
    activeThread:    string | null
    onNewChat:       () => void
    onSelectThread:  (t: ThreadEntry) => void
    collapsed:       boolean
    onToggleCollapse: () => void
}

export default function Sidebar({ threads, activeThread, onNewChat, onSelectThread, collapsed, onToggleCollapse }: Props) {
    return (
        <aside id="sidebar" className={collapsed ? "collapsed" : ""}>
            <div id="sidebar-inner">
                <div id="sidebar-header">
                    <img id="sidebar-wordmark" src="/images/logo-white.png" alt="A·R·I" />
                    <button id="btn-toggle-sidebar" title="Close sidebar" onClick={onToggleCollapse}>
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/>
                        </svg>
                    </button>
                </div>

                <button id="btn-new-chat" onClick={onNewChat}>
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>
                    </svg>
                    New chat
                </button>

                <div id="sidebar-section-label">Recents</div>
                <ul id="thread-list">
                    {threads.map(t => {
                        const label = t.isInternal
                            ? (t.agentName ?? "Internal")
                            : t.key.startsWith("web-") ? "Web chat" : "Discord"
                        const time = t.lastMessageAt && !t.lastMessageAt.startsWith("0001")
                            ? new Date(t.lastMessageAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
                            : ""
                        const classes = [
                            t.key === activeThread ? "active" : "",
                            t.isInternal ? "internal-thread" : "",
                            t.state === "inactive" || t.state === "dormant" ? "inactive" : "",
                        ].filter(Boolean).join(" ")
                        return (
                            <li key={t.key} className={classes} onClick={() => onSelectThread(t)}>
                                <span className="thread-name">{label}</span>
                                <span className="thread-time">{time}</span>
                            </li>
                        )
                    })}
                </ul>

                <div id="sidebar-footer">
                    <a id="btn-control-panel" href="/controlpanel.html">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <circle cx="12" cy="12" r="3"/>
                            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
                        </svg>
                        Control Panel
                    </a>
                </div>
            </div>
        </aside>
    )
}
