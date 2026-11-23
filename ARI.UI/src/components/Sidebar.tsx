import { useState } from "react"
import { EyeIcon, BrainIcon, SpeechIcon } from "./ThreadIcons"
import type { ThreadEntry } from "../hooks/useThreads"
interface Props {
    threads:             ThreadEntry[]
    unreadThreads?:      Set<string>
    activeThread:        string | null
    activeView:          "chat" | "projects"
    onNewChat:           () => void
    onOpenProjects:      () => void
    onSelectThread:      (t: ThreadEntry) => void
    onCloseThread:       (t: ThreadEntry) => void
    collapsed:           boolean
    onToggleCollapse:    () => void
    serverVersion:       string | null
    outdated:            boolean
    userDisplayName?:    string
    onOpenPreferences?:  () => void
    isAdmin?:            boolean
    onRenameThread?:     (t: ThreadEntry) => void
    onHideThread?:       (t: ThreadEntry) => void
}

export default function Sidebar({ threads, unreadThreads, activeThread, activeView, onNewChat, onOpenProjects, onSelectThread, onCloseThread, collapsed, onToggleCollapse, serverVersion, outdated, userDisplayName, onOpenPreferences, isAdmin, onRenameThread, onHideThread }: Props) {
    const [menu, setMenu] = useState<{ key: string; x: number; y: number } | null>(null)
    return (
        <aside id="sidebar" className={collapsed ? "collapsed" : ""}>
            <div id="sidebar-inner">
                <div id="sidebar-header">
                    <div id="sidebar-header-left">
                        <img id="sidebar-wordmark" src="/images/logo-white.png?v=2" alt="A·R·I" />
                        {serverVersion && <span id="sidebar-version" title="A·R·I server version">{`v${serverVersion}`}</span>}
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
                <div className="thread-scroll">{(() => {
                    const renderThread = (t: ThreadEntry) => {
const baseName = t.isInternal
                            ? (t.agentName ?? "Internal")
                            : t.key.startsWith("client-") ? "Desktop"
                            : t.key.startsWith("web-")    ? "Web chat" : "Discord"
                        const time = t.lastMessageAt && !t.lastMessageAt.startsWith("0001")
                            ? new Date(t.lastMessageAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
                            : ""
                        const classes = [
                            t.key === activeThread ? "active" : "",
                            t.isInternal ? "internal-thread" : "",
                            t.state === "inactive" || t.state === "dormant" || t.state === "expired" ? "inactive" : "",
                        ].filter(Boolean).join(" ")
                        return (
                            <li key={t.key} className={classes} onClick={() => onSelectThread(t)}
                                onContextMenu={e => { if (t.isInternal) return; e.preventDefault(); e.stopPropagation(); setMenu({ key: t.key, x: e.clientX, y: e.clientY }) }}>
                                {/* The icon's slot (and the indent it brings) is only there while there is an icon to show. */}
                                {(() => {
                                    const icon = t.status === "prefilling" || t.status === "researching" ? <span className="thread-state-fade" title="Reading"><EyeIcon size={18} /></span>
                                        : t.status === "thinking" ? <span className="thread-state-fade" title="Thinking"><BrainIcon size={18} /></span>
                                        : t.status === "typing" ? <span className="thread-state-fade" title="Typing"><SpeechIcon size={18} /></span>
                                        : unreadThreads?.has(t.key) ? <span className="thread-unread-dot" title="Unread message" />
                                        : null
                                    return icon && <span className="thread-state" aria-hidden="true">{icon}</span>
                                })()}
                                <span className="thread-name">
                                    {t.projectName && (
                                        <span className="thread-project">{t.projectName}/</span>
                                    )}
                                    {!t.isInternal && t.title ? t.title : baseName}
                                </span>
                                <span className="thread-meta">
                                    {t.isCodeMode && (
                                        <svg className="thread-code-icon" width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                                            <polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/>
                                        </svg>
                                    )}
                                    <span className="thread-time">{time}</span>
                                    {!t.isInternal && (
                                        <button
                                            className="thread-close"
                                            title={t.state === "expired" ? "Delete this saved conversation forever" : "Close thread (saves to memory, then removes it)"}
                                            onClick={e => { e.stopPropagation(); onCloseThread(t) }}
                                        >
                                            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                                                <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>
                                            </svg>
                                        </button>
                                    )}
                                </span>
                            </li>
                        )
                    
                    }
                    const mine   = threads.filter(t => !t.ownerName)
                    const others = new Map<string, ThreadEntry[]>()
                    threads.filter(t => t.ownerName).forEach(t => others.set(t.ownerName!, [...(others.get(t.ownerName!) ?? []), t]))
                    return (
                        <>
                            <ul id="thread-list">{mine.map(renderThread)}</ul>
                            {[...others.entries()].map(([name, list]) => (
                                <div key={name} className="thread-owner-group">
                                    <div className="sidebar-section-label thread-owner-label" title="Only you, as admin, can see these. They can't see your chats.">{name}</div>
                                    <ul className="thread-list">{list.map(renderThread)}</ul>
                                </div>
                            ))}
                        </>
                    )
                })()}</div>

                <div id="sidebar-footer">
                    {userDisplayName && (
                        <button id="btn-user-prefs" onClick={onOpenPreferences}>
                            <span id="btn-user-avatar">{userDisplayName[0].toUpperCase()}</span>
                            <span id="btn-user-name">{userDisplayName}</span>
                        </button>
                    )}
                    {isAdmin && <a id="btn-control-panel" href="/controlpanel.html">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <circle cx="12" cy="12" r="3"/>
                            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
                        </svg>
                        Control Panel
                    </a>}
                    {outdated && (
                        <div id="sidebar-update-banner">
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/>
                                <line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
                            </svg>
                            Server outdated — a newer version is available
                        </div>
                    )}
                </div>
            </div>

            {menu && (() => {
                const t = threads.find(x => x.key === menu.key)
                if (!t) return null
                const close = () => setMenu(null)
                return (
                    <>
                        <div className="thread-menu-backdrop" onClick={close} onContextMenu={e => { e.preventDefault(); close() }} />
                        <ul className="thread-menu" style={{ left: menu.x, top: menu.y }}>
                            <li onClick={() => { close(); onRenameThread?.(t) }}>Rename</li>
                            <li onClick={() => { close(); onHideThread?.(t) }}>Hide</li>
                        </ul>
                    </>
                )
            })()}
        </aside>
    )
}
