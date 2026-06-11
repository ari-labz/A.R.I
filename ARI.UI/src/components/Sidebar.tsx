import type { ThreadEntry } from "../hooks/useThreads"

interface Props {
    threads:          ThreadEntry[]
    activeThread:     string | null
    activeView:       "chat" | "projects"
    onNewChat:        () => void
    onOpenProjects:   () => void
    onSelectThread:   (t: ThreadEntry) => void
    collapsed:        boolean
    onToggleCollapse: () => void
    clientVersion:    string | null
    outdated:         boolean
}

export default function Sidebar({ threads, activeThread, activeView, onNewChat, onOpenProjects, onSelectThread, collapsed, onToggleCollapse, clientVersion, outdated }: Props) {
    return (
        <aside id="sidebar" className={collapsed ? "collapsed" : ""}>
            <div id="sidebar-inner">
                <div id="sidebar-header">
                    <div id="sidebar-header-left">
                        <img id="sidebar-wordmark" src="/images/logo-white.png" alt="A·R·I" />
                        {clientVersion && <span id="sidebar-version">{`v${clientVersion}`}</span>}
                    </div>
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

                <button
                    id="btn-projects"
                    className={activeView === "projects" ? "active" : ""}
                    onClick={onOpenProjects}
                >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                    </svg>
                    Projects
                </button>

                <div id="sidebar-section-label">Recents</div>
                <ul id="thread-list">
                    {threads.map(t => {
                        const baseName = t.isInternal
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
                                <span className="thread-name">
                                    {t.projectName && (
                                        <span className="thread-project">{t.projectName}/</span>
                                    )}
                                    {baseName}
                                </span>
                                <span className="thread-meta">
                                    {t.isCodeMode && (
                                        <svg className="thread-code-icon" width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                                            <polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/>
                                        </svg>
                                    )}
                                    <span className="thread-time">{time}</span>
                                </span>
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
                    {(outdated || true) && (
                        <div id="sidebar-update-banner">
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/>
                                <line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
                            </svg>
                            Client outdated — update to avoid issues
                        </div>
                    )}
                </div>
            </div>
        </aside>
    )
}
