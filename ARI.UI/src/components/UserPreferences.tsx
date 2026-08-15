import { useState, useEffect } from "react"
import { apiFetch, changePassword, logout, type AuthUser } from "../auth"

interface Session {
    sessionId:  string
    deviceHint: string
    isDesktop:  boolean
    lastUsedAt: string
}

interface Props {
    user:          AuthUser
    onClose:       () => void
    onLogout:      () => void
    onUserUpdated: (u: AuthUser) => void
}

export default function UserPreferences({ user, onClose, onLogout, onUserUpdated }: Props) {
    const [displayName,       setDisplayName]       = useState(user.displayName)
    const [nameLoading,       setNameLoading]        = useState(false)
    const [nameError,         setNameError]          = useState<string | null>(null)
    const [nameSaved,         setNameSaved]          = useState(false)

    const [pwOpen,            setPwOpen]             = useState(false)
    const [currentPw,         setCurrentPw]          = useState("")
    const [newPw,             setNewPw]              = useState("")
    const [confirmPw,         setConfirmPw]          = useState("")
    const [pwLoading,         setPwLoading]          = useState(false)
    const [pwError,           setPwError]            = useState<string | null>(null)
    const [pwSaved,           setPwSaved]            = useState(false)

    const [sessions,          setSessions]           = useState<Session[]>([])
    const [sessionsLoading,   setSessionsLoading]    = useState(true)

    useEffect(() => {
        apiFetch("/user/preferences").then(r => r.ok ? r.json() : null).then(data => {
            if (data?.sessions) setSessions(data.sessions)
        }).catch(() => {}).finally(() => setSessionsLoading(false))
    }, [])

    async function saveName(e: React.FormEvent) {
        e.preventDefault()
        const name = displayName.trim()
        if (!name) return
        setNameError(null)
        setNameLoading(true)
        setNameSaved(false)
        try {
            const res = await apiFetch("/user/preferences", {
                method:  "PUT",
                headers: { "Content-Type": "application/json" },
                body:    JSON.stringify({ displayName: name }),
            })
            if (!res.ok) {
                const d = await res.json().catch(() => ({}))
                setNameError((d as { error?: string }).error ?? "Save failed")
                return
            }
            onUserUpdated({ ...user, displayName: name })
            setNameSaved(true)
            setTimeout(() => setNameSaved(false), 2500)
        } catch {
            setNameError("Save failed")
        } finally {
            setNameLoading(false)
        }
    }

    async function savePassword(e: React.FormEvent) {
        e.preventDefault()
        if (newPw.length < 8) { setPwError("At least 8 characters required."); return }
        if (newPw !== confirmPw) { setPwError("Passwords do not match."); return }
        setPwError(null)
        setPwLoading(true)
        setPwSaved(false)
        try {
            await changePassword(currentPw, newPw)
            setCurrentPw(""); setNewPw(""); setConfirmPw("")
            setPwSaved(true)
            setTimeout(() => { setPwSaved(false); setPwOpen(false) }, 2000)
        } catch (err) {
            setPwError(err instanceof Error ? err.message : "Failed to change password")
        } finally {
            setPwLoading(false)
        }
    }

    async function revokeSession(id: string) {
        await apiFetch(`/user/sessions/${encodeURIComponent(id)}`, { method: "DELETE" }).catch(() => {})
        setSessions(prev => prev.filter(s => s.sessionId !== id))
    }

    async function handleLogout() {
        await logout()
        onLogout()
    }

    function formatDate(iso: string) {
        try { return new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) }
        catch { return iso }
    }

    return (
        <div style={overlay} onClick={onClose}>
            <div style={panel} onClick={e => e.stopPropagation()}>
                {/* Header */}
                <div style={header}>
                    <span style={headerTitle}>Preferences</span>
                    <button style={closeBtn} onClick={onClose} aria-label="Close">
                        <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                            <line x1="2" y1="2" x2="14" y2="14"/><line x1="14" y1="2" x2="2" y2="14"/>
                        </svg>
                    </button>
                </div>

                <div style={body}>
                    {/* User info */}
                    <div style={section}>
                        <p style={sectionLabel}>ACCOUNT</p>
                        <p style={accountLine}><span style={accountUsername}>{user.username}</span><span style={rolePill(user.role)}>{user.role}</span></p>
                    </div>

                    {/* Display name */}
                    <div style={section}>
                        <p style={sectionLabel}>DISPLAY NAME</p>
                        <form onSubmit={saveName} style={{ display: "flex", gap: 8 }}>
                            <input
                                style={inputStyle}
                                value={displayName}
                                onChange={e => setDisplayName(e.target.value)}
                                maxLength={40}
                                disabled={nameLoading}
                            />
                            <button type="submit" style={saveBtn(nameLoading)} disabled={nameLoading}>
                                {nameSaved ? "Saved" : "Save"}
                            </button>
                        </form>
                        {nameError && <p style={errorStyle}>{nameError}</p>}
                    </div>

                    {/* Change password accordion */}
                    <div style={section}>
                        <button style={accordionBtn} onClick={() => { setPwOpen(o => !o); setPwError(null) }}>
                            <span>Change password</span>
                            <svg style={{ transform: pwOpen ? "rotate(180deg)" : "none", transition: "transform 0.2s" }} width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                                <polyline points="2,5 7,10 12,5"/>
                            </svg>
                        </button>
                        {pwOpen && (
                            <form onSubmit={savePassword} style={pwForm}>
                                <input style={inputStyle} type="password" placeholder="Current password" autoComplete="current-password" value={currentPw} onChange={e => setCurrentPw(e.target.value)} disabled={pwLoading} />
                                <input style={inputStyle} type="password" placeholder="New password"     autoComplete="new-password"     value={newPw}     onChange={e => setNewPw(e.target.value)}     disabled={pwLoading} />
                                <input style={inputStyle} type="password" placeholder="Confirm password" autoComplete="new-password"     value={confirmPw} onChange={e => setConfirmPw(e.target.value)} disabled={pwLoading} />
                                {pwError  && <p style={errorStyle}>{pwError}</p>}
                                {pwSaved  && <p style={successStyle}>Password changed.</p>}
                                <button type="submit" style={saveBtn(pwLoading)} disabled={pwLoading}>
                                    {pwLoading ? "Saving…" : "Change password"}
                                </button>
                            </form>
                        )}
                    </div>

                    {/* Active sessions */}
                    <div style={section}>
                        <p style={sectionLabel}>ACTIVE SESSIONS</p>
                        {sessionsLoading ? (
                            <p style={mutedText}>Loading…</p>
                        ) : sessions.length === 0 ? (
                            <p style={mutedText}>No active sessions.</p>
                        ) : (
                            <div style={sessionList}>
                                {sessions.map(s => (
                                    <div key={s.sessionId} style={sessionRow}>
                                        <div>
                                            <span style={sessionDevice}>{s.deviceHint || (s.isDesktop ? "Desktop" : "Browser")}</span>
                                            <span style={sessionDate}>{formatDate(s.lastUsedAt)}</span>
                                        </div>
                                        <button style={revokeBtn} onClick={() => revokeSession(s.sessionId)}>Revoke</button>
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                </div>

                {/* Footer */}
                <div style={footer}>
                    <button style={logoutBtn} onClick={handleLogout}>Sign out</button>
                </div>
            </div>
        </div>
    )
}

const overlay: React.CSSProperties = {
    position:       "fixed",
    inset:          0,
    background:     "rgba(16,22,24,0.35)",
    zIndex:         9000,
    display:        "flex",
    alignItems:     "center",
    justifyContent: "center",
}

const panel: React.CSSProperties = {
    background:    "#ffffff",
    borderRadius:  16,
    boxShadow:     "0 8px 48px rgba(16,22,24,0.14)",
    width:         420,
    maxWidth:      "calc(100vw - 32px)",
    maxHeight:     "calc(100vh - 48px)",
    display:       "flex",
    flexDirection: "column",
    overflow:      "hidden",
}

const header: React.CSSProperties = {
    display:        "flex",
    alignItems:     "center",
    justifyContent: "space-between",
    padding:        "20px 24px 16px",
    borderBottom:   "1px solid #edf0f1",
}

const headerTitle: React.CSSProperties = {
    fontSize:   16,
    fontWeight: 600,
    color:      "#101618",
}

const closeBtn: React.CSSProperties = {
    background: "none",
    border:     "none",
    cursor:     "pointer",
    color:      "#8a9598",
    display:    "flex",
    padding:    4,
    borderRadius: 6,
}

const body: React.CSSProperties = {
    flex:       1,
    overflowY:  "auto",
    padding:    "4px 24px 8px",
}

const footer: React.CSSProperties = {
    padding:    "12px 24px 20px",
    borderTop:  "1px solid #edf0f1",
}

const section: React.CSSProperties = {
    marginBottom: 24,
    paddingTop:   20,
}

const sectionLabel: React.CSSProperties = {
    margin:       "0 0 8px",
    fontSize:     11,
    fontWeight:   600,
    letterSpacing: "0.08em",
    color:        "#8a9598",
}

const accountLine: React.CSSProperties = {
    display:    "flex",
    alignItems: "center",
    gap:        10,
    margin:     0,
}

const accountUsername: React.CSSProperties = {
    fontSize:   15,
    fontWeight: 500,
    color:      "#101618",
}

function rolePill(role: string): React.CSSProperties {
    const isAdmin = role === "Admin"
    return {
        padding:      "2px 8px",
        borderRadius: 100,
        fontSize:     11,
        fontWeight:   600,
        background:   isAdmin ? "#101618" : "#edf0f1",
        color:        isAdmin ? "#cadde2" : "#6b7a80",
    }
}

const inputStyle: React.CSSProperties = {
    flex:         1,
    padding:      "9px 12px",
    borderRadius: 8,
    border:       "1.5px solid #dde3e6",
    fontSize:     14,
    outline:      "none",
    background:   "#f9fafb",
    color:        "#101618",
    minWidth:     0,
    boxSizing:    "border-box",
    width:        "100%",
}

function saveBtn(disabled: boolean): React.CSSProperties {
    return {
        flexShrink:   0,
        padding:      "9px 16px",
        borderRadius: 8,
        border:       "none",
        background:   disabled ? "#8a9598" : "#101618",
        color:        "#cadde2",
        fontSize:     13,
        fontWeight:   600,
        cursor:       disabled ? "not-allowed" : "pointer",
        whiteSpace:   "nowrap",
    }
}

const accordionBtn: React.CSSProperties = {
    display:        "flex",
    alignItems:     "center",
    justifyContent: "space-between",
    width:          "100%",
    padding:        "10px 14px",
    borderRadius:   8,
    border:         "1.5px solid #dde3e6",
    background:     "#f9fafb",
    color:          "#101618",
    fontSize:       14,
    cursor:         "pointer",
    textAlign:      "left",
}

const pwForm: React.CSSProperties = {
    display:       "flex",
    flexDirection: "column",
    gap:           10,
    marginTop:     10,
}

const errorStyle: React.CSSProperties = {
    margin:     0,
    padding:    "8px 12px",
    borderRadius: 6,
    background: "#fff1f0",
    border:     "1px solid #ffccc7",
    color:      "#cf1322",
    fontSize:   13,
}

const successStyle: React.CSSProperties = {
    margin:     0,
    padding:    "8px 12px",
    borderRadius: 6,
    background: "#f6ffed",
    border:     "1px solid #b7eb8f",
    color:      "#389e0d",
    fontSize:   13,
}

const sessionList: React.CSSProperties = {
    display:       "flex",
    flexDirection: "column",
    gap:           8,
}

const sessionRow: React.CSSProperties = {
    display:        "flex",
    alignItems:     "center",
    justifyContent: "space-between",
    padding:        "10px 14px",
    borderRadius:   8,
    background:     "#f9fafb",
    border:         "1px solid #edf0f1",
}

const sessionDevice: React.CSSProperties = {
    display:    "block",
    fontSize:   13,
    fontWeight: 500,
    color:      "#101618",
}

const sessionDate: React.CSSProperties = {
    display:  "block",
    fontSize: 12,
    color:    "#8a9598",
    marginTop: 2,
}

const revokeBtn: React.CSSProperties = {
    padding:      "5px 10px",
    borderRadius: 6,
    border:       "1px solid #ffccc7",
    background:   "#fff1f0",
    color:        "#cf1322",
    fontSize:     12,
    cursor:       "pointer",
}

const mutedText: React.CSSProperties = {
    margin:   0,
    fontSize: 13,
    color:    "#8a9598",
}

const logoutBtn: React.CSSProperties = {
    width:        "100%",
    padding:      "11px 0",
    borderRadius: 8,
    border:       "1.5px solid #dde3e6",
    background:   "transparent",
    color:        "#6b7a80",
    fontSize:     14,
    fontWeight:   500,
    cursor:       "pointer",
}
