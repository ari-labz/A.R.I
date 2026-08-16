import { useState, useRef, useEffect } from "react"
import { login, changePassword, setToken, type AuthUser, type LoginResult } from "../auth"
import { env } from "../env"

interface Props {
    onLoginSuccess: (user: AuthUser, token: string) => void
}

type Screen = "login" | "changePassword"

export default function LoginScreen({ onLoginSuccess }: Props) {
    const [screen,          setScreen]          = useState<Screen>("login")
    const [username,        setUsername]         = useState("")
    const [password,        setPassword]         = useState("")
    const [newPassword,     setNewPassword]      = useState("")
    const [confirmPassword, setConfirmPassword]  = useState("")
    const [error,           setError]            = useState<string | null>(null)
    const [loading,         setLoading]          = useState(false)
    // Keep the token from the initial login so we can call change-password with it
    const pendingResult = useRef<LoginResult | null>(null)

    const usernameRef = useRef<HTMLInputElement>(null)
    useEffect(() => { usernameRef.current?.focus() }, [])

    async function handleLogin(e: React.FormEvent) {
        e.preventDefault()
        if (!username.trim() || !password) return
        setError(null)
        setLoading(true)
        try {
            const result = await login(username.trim(), password, env.isDesktop)
            if (result.mustChangePassword) {
                // Store the token temporarily so the change-password call is authed
                setToken(result.token)
                pendingResult.current = result
                setScreen("changePassword")
            } else {
                setToken(result.token)
                onLoginSuccess({
                    id:                 0,
                    username:           username.trim(),
                    role:               result.role,
                    displayName:        result.displayName,
                    mustChangePassword: false,
                }, result.token)
            }
        } catch (err) {
            setError(err instanceof Error ? err.message : "Login failed")
        } finally {
            setLoading(false)
        }
    }

    async function handleChangePassword(e: React.FormEvent) {
        e.preventDefault()
        if (newPassword.length < 8) { setError("Password must be at least 8 characters."); return }
        if (newPassword !== confirmPassword) { setError("Passwords do not match."); return }
        setError(null)
        setLoading(true)
        try {
            await changePassword(password, newPassword)
            const r = pendingResult.current!
            onLoginSuccess({
                id:                 0,
                username:           username.trim(),
                role:               r.role,
                displayName:        r.displayName,
                mustChangePassword: false,
            }, r.token)
        } catch (err) {
            setError(err instanceof Error ? err.message : "Could not change password")
        } finally {
            setLoading(false)
        }
    }

    return (
        <div style={overlay}>
            <div style={card}>
                <div style={logoRow}>
                    <img src="/images/logo-black.png" alt="A·R·I" style={logoImg} />
                </div>

                {screen === "login" ? (
                    <form onSubmit={handleLogin} style={form}>
                        <p style={subtitle}>Sign in to continue</p>
                        <input
                            ref={usernameRef}
                            style={inputStyle}
                            type="text"
                            placeholder="Username"
                            autoComplete="username"
                            value={username}
                            onChange={e => setUsername(e.target.value)}
                            disabled={loading}
                        />
                        <input
                            style={inputStyle}
                            type="password"
                            placeholder="Password"
                            autoComplete="current-password"
                            value={password}
                            onChange={e => setPassword(e.target.value)}
                            disabled={loading}
                        />
                        {error && <p style={errorStyle}>{error}</p>}
                        <button type="submit" style={btn(loading)} disabled={loading}>
                            {loading ? "Signing in…" : "Sign in"}
                        </button>
                    </form>
                ) : (
                    <form onSubmit={handleChangePassword} style={form}>
                        <p style={subtitle}>Set a new password</p>
                        <p style={hintText}>Your account requires a password change before continuing.</p>
                        <input
                            style={inputStyle}
                            type="password"
                            placeholder="New password"
                            autoComplete="new-password"
                            value={newPassword}
                            autoFocus
                            onChange={e => setNewPassword(e.target.value)}
                            disabled={loading}
                        />
                        <input
                            style={inputStyle}
                            type="password"
                            placeholder="Confirm new password"
                            autoComplete="new-password"
                            value={confirmPassword}
                            onChange={e => setConfirmPassword(e.target.value)}
                            disabled={loading}
                        />
                        {error && <p style={errorStyle}>{error}</p>}
                        <button type="submit" style={btn(loading)} disabled={loading}>
                            {loading ? "Saving…" : "Set password"}
                        </button>
                        <button
                            type="button"
                            style={backBtn}
                            onClick={() => { setScreen("login"); setError(null); setNewPassword(""); setConfirmPassword("") }}
                        >
                            Back
                        </button>
                    </form>
                )}
            </div>
        </div>
    )
}

const overlay: React.CSSProperties = {
    position:       "fixed",
    inset:          0,
    display:        "flex",
    alignItems:     "center",
    justifyContent: "center",
    background:     "#f4f6f7",
    zIndex:         9999,
}

const card: React.CSSProperties = {
    background:   "#ffffff",
    borderRadius: 16,
    boxShadow:    "0 4px 32px rgba(16,22,24,0.10)",
    padding:      "40px 40px 36px",
    width:        360,
    maxWidth:     "calc(100vw - 32px)",
}

const logoRow: React.CSSProperties = {
    display:         "flex",
    justifyContent:  "center",
    alignItems:      "center",
    marginBottom:    28,
    background:      "#ffffff",
    borderRadius:    10,
    padding:         "18px 24px",
}

const logoImg: React.CSSProperties = {
    height:   40,
    width:    "auto",
    display:  "block",
}

const subtitle: React.CSSProperties = {
    margin:     "0 0 20px",
    fontSize:   14,
    color:      "#6b7a80",
    textAlign:  "center",
}

const hintText: React.CSSProperties = {
    margin:     "-8px 0 20px",
    fontSize:   13,
    color:      "#8a9598",
    textAlign:  "center",
    lineHeight: 1.5,
}

const form: React.CSSProperties = {
    display:       "flex",
    flexDirection: "column",
    gap:           12,
}

const inputStyle: React.CSSProperties = {
    padding:      "11px 14px",
    borderRadius: 8,
    border:       "1.5px solid #dde3e6",
    fontSize:     14,
    outline:      "none",
    background:   "#f9fafb",
    color:        "#101618",
    transition:   "border-color 0.15s",
    width:        "100%",
    boxSizing:    "border-box",
}

function btn(disabled: boolean): React.CSSProperties {
    return {
        marginTop:    4,
        padding:      "12px 0",
        borderRadius: 8,
        border:       "none",
        background:   disabled ? "#8a9598" : "#223742",
        color:        "#cadde2",
        fontSize:     14,
        fontWeight:   600,
        cursor:       disabled ? "not-allowed" : "pointer",
        transition:   "background 0.15s",
        letterSpacing: "0.03em",
    }
}

const backBtn: React.CSSProperties = {
    padding:      "10px 0",
    borderRadius: 8,
    border:       "1.5px solid #dde3e6",
    background:   "transparent",
    color:        "#6b7a80",
    fontSize:     13,
    cursor:       "pointer",
}

const errorStyle: React.CSSProperties = {
    margin:     "0",
    padding:    "10px 12px",
    borderRadius: 6,
    background: "#fff1f0",
    border:     "1px solid #ffccc7",
    color:      "#cf1322",
    fontSize:   13,
}
