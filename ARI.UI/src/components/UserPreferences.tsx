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

    const [unOpen,            setUnOpen]             = useState(false)
    const [newUsername,       setNewUsername]         = useState("")
    const [unPassword,        setUnPassword]          = useState("")
    const [unLoading,         setUnLoading]           = useState(false)
    const [unError,           setUnError]             = useState<string | null>(null)

    const [sessions,          setSessions]           = useState<Session[]>([])
    const [sessionsLoading,   setSessionsLoading]    = useState(true)

    // Commands ARI may run without asking ("allow every time"), global across projects. Admin only.
    const isAdmin = user.role === "Admin"
    const [allowed,           setAllowed]            = useState<string[]>([])
    const [allowedLoading,    setAllowedLoading]     = useState(isAdmin)
    const [allowedError,      setAllowedError]       = useState<string | null>(null)

    useEffect(() => {
        apiFetch("/api/v1/user/preferences").then(r => r.ok ? r.json() : null).then(data => {
            if (data?.sessions) setSessions(data.sessions)
        }).catch(() => {}).finally(() => setSessionsLoading(false))
    }, [])

    useEffect(() => {
        if (!isAdmin) return
        apiFetch("/api/v1/command-allowlist").then(r => r.ok ? r.json() : null).then(data => {
            if (Array.isArray(data?.commands)) setAllowed(data.commands)
        }).catch(() => {}).finally(() => setAllowedLoading(false))
    }, [isAdmin])

    async function saveAllowed(next: string[]) {
        setAllowedError(null)
        try {
            const res = await apiFetch("/api/v1/command-allowlist", {
                method: "PUT", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ commands: next }),
            })
            if (!res.ok) { setAllowedError("Couldn't update the list."); return }
            setAllowed(next)
        } catch { setAllowedError("Couldn't update the list.") }
    }

    async function saveName(e: React.FormEvent) {
        e.preventDefault()
        const name = displayName.trim()
        if (!name) return
        setNameError(null)
        setNameLoading(true)
        setNameSaved(false)
        try {
            const res = await apiFetch("/api/v1/user/preferences", {
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

    async function changeUsername(e: React.FormEvent) {
        e.preventDefault()
        const name = newUsername.trim()
        if (!name) { setUnError("Username cannot be empty."); return }
        setUnError(null)
        setUnLoading(true)
        try {
            const res = await apiFetch("/api/v1/user/username", {
                method:  "PUT",
                headers: { "Content-Type": "application/json" },
                body:    JSON.stringify({ newUsername: name, currentPassword: unPassword }),
            })
            const d = await res.json().catch(() => ({}))
            if (!res.ok) { setUnError((d as { error?: string }).error ?? "Failed"); return }
            // Server revoked all sessions — force re-login with the new username visible.
            onLogout()
        } finally {
            setUnLoading(false)
        }
    }

    async function revokeSession(id: string) {
        await apiFetch(`/api/v1/user/sessions/${encodeURIComponent(id)}`, { method: "DELETE" }).catch(() => {})
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

                    {/* Change username accordion */}
                    <div style={section}>
                        <button style={accordionBtn} onClick={() => { setUnOpen(o => !o); setUnError(null) }}>
                            <span>Change username</span>
                            <svg style={{ transform: unOpen ? "rotate(180deg)" : "none", transition: "transform 0.2s" }} width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                                <polyline points="2,5 7,10 12,5"/>
                            </svg>
                        </button>
                        {unOpen && (
                            <form onSubmit={changeUsername} style={pwForm}>
                                <input style={inputStyle} type="text"     placeholder="New username"     autoComplete="username"         value={newUsername} onChange={e => setNewUsername(e.target.value)}  disabled={unLoading} />
                                <input style={inputStyle} type="password" placeholder="Current password" autoComplete="current-password" value={unPassword}  onChange={e => setUnPassword(e.target.value)}   disabled={unLoading} />
                                {unError && <p style={errorStyle}>{unError}</p>}
                                <p style={{ ...mutedText, marginTop: 0 }}>You'll be signed out after changing your username.</p>
                                <button type="submit" style={saveBtn(unLoading)} disabled={unLoading}>
                                    {unLoading ? "Saving…" : "Change username"}
                                </button>
                            </form>
                        )}
                    </div>

                    {/* Commands ARI may run without asking */}
                    {isAdmin && (
                        <div style={section}>
                            <p style={sectionLabel}>ALLOWED COMMANDS</p>
                            <p style={{ ...mutedText, marginTop: 0 }}>Commands ARI runs without asking, in every project. Dangerous ones (like git reset --hard) always ask and never appear here.</p>
                            {allowedLoading ? (
                                <p style={mutedText}>Loading…</p>
                            ) : allowed.length === 0 ? (
                                <p style={mutedText}>None yet — you'll be asked once per new command.</p>
                            ) : (
                                <div style={sessionList}>
                                    {allowed.map(cmd => (
                                        <div key={cmd} style={sessionRow}>
                                            <span style={sessionDevice}>{cmd}</span>
                                            <button style={revokeBtn} onClick={() => saveAllowed(allowed.filter(c => c !== cmd))}>Remove</button>
                                        </div>
                                    ))}
                                </div>
                            )}
                            {allowedError && <p style={errorStyle}>{allowedError}</p>}
                        </div>
                    )}

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
    background:     "var(--col-user-preferences-line-294-background)",
    zIndex:         9000,
    display:        "flex",
    alignItems:     "center",
    justifyContent: "center",
}

const panel: React.CSSProperties = {
    background:    "var(--col-user-preferences-line-302-background)",
    borderRadius:  16,
    boxShadow:     "0 8px 48px var(--col-user-preferences-line-304-shadow)",
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
    borderBottom:   "1px solid var(--col-user-preferences-line-318-border)",
}

const headerTitle: React.CSSProperties = {
    fontSize:   16,
    fontWeight: 600,
    color:      "var(--col-user-preferences-line-324-text)",
}

const closeBtn: React.CSSProperties = {
    background: "none",
    border:     "none",
    cursor:     "pointer",
    color:      "var(--col-user-preferences-line-331-text)",
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
    borderTop:  "1px solid var(--col-user-preferences-line-345-border)",
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
    color:        "var(--col-user-preferences-line-358-text)",
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
    color:      "var(--col-user-preferences-line-371-text)",
}

function rolePill(role: string): React.CSSProperties {
    const isAdmin = role === "Admin"
    return {
        padding:      "2px 8px",
        borderRadius: 100,
        fontSize:     11,
        fontWeight:   600,
        background:   isAdmin ? "var(--col-user-preferences-line-381-background)" : "var(--col-user-preferences-line-381-colour)",
        color:        isAdmin ? "var(--col-user-preferences-line-382-text)" : "var(--col-user-preferences-line-382-colour)",
    }
}

const inputStyle: React.CSSProperties = {
    flex:         1,
    padding:      "9px 12px",
    borderRadius: 8,
    border:       "1.5px solid var(--col-user-preferences-line-390-border)",
    fontSize:     14,
    outline:      "none",
    background:   "var(--col-user-preferences-line-393-background)",
    color:        "var(--col-user-preferences-line-394-text)",
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
        background:   disabled ? "var(--col-user-preferences-line-406-background)" : "var(--col-user-preferences-line-406-colour)",
        color:        "var(--col-user-preferences-line-407-text)",
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
    border:         "1.5px solid var(--col-user-preferences-line-422-border)",
    background:     "var(--col-user-preferences-line-423-background)",
    color:          "var(--col-user-preferences-line-424-text)",
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
    background: "var(--col-user-preferences-line-441-background)",
    border:     "1px solid var(--col-user-preferences-line-442-border)",
    color:      "var(--col-user-preferences-line-443-text)",
    fontSize:   13,
}

const successStyle: React.CSSProperties = {
    margin:     0,
    padding:    "8px 12px",
    borderRadius: 6,
    background: "var(--col-user-preferences-line-451-background)",
    border:     "1px solid var(--col-user-preferences-line-452-border)",
    color:      "var(--col-user-preferences-line-453-text)",
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
    background:     "var(--col-user-preferences-line-469-background)",
    border:         "1px solid var(--col-user-preferences-line-470-border)",
}

const sessionDevice: React.CSSProperties = {
    display:    "block",
    fontSize:   13,
    fontWeight: 500,
    color:      "var(--col-user-preferences-line-477-text)",
}

const sessionDate: React.CSSProperties = {
    display:  "block",
    fontSize: 12,
    color:    "var(--col-user-preferences-line-483-text)",
    marginTop: 2,
}

const revokeBtn: React.CSSProperties = {
    padding:      "5px 10px",
    borderRadius: 6,
    border:       "1px solid var(--col-user-preferences-line-490-border)",
    background:   "var(--col-user-preferences-line-491-background)",
    color:        "var(--col-user-preferences-line-492-text)",
    fontSize:     12,
    cursor:       "pointer",
}

const mutedText: React.CSSProperties = {
    margin:   0,
    fontSize: 13,
    color:    "var(--col-user-preferences-line-500-text)",
}

const logoutBtn: React.CSSProperties = {
    width:        "100%",
    padding:      "11px 0",
    borderRadius: 8,
    border:       "1.5px solid var(--col-user-preferences-line-507-border)",
    background:   "transparent",
    color:        "var(--col-user-preferences-line-509-text)",
    fontSize:     14,
    fontWeight:   500,
    cursor:       "pointer",
}
