import { useEffect, useRef, useState } from "react"
import { apiFetch, setToken, type AuthUser } from "../auth"

interface Props {
    /** "account" on a fresh install, "model" when an admin exists but the wizard never finished. */
    startStep: "account" | "model"
    existingUser: AuthUser | null
    onFinished: (user: AuthUser, token: string | null) => void
}

interface Hardware { totalBytes: number; usableBytes: number; availableBytes: number; unified: boolean; gpu: { name: string; vramBytes: number } | null; note: string }
interface ModelFile { path: string; name: string; sizeBytes: number; quant: string; downloadUrl: string; fit: "fits" | "tight" | "no"; maxContext: number; needBytesAt8k: number }
interface Inspect { repo: string; kvExact: boolean; hardware: Hardware; files: ModelFile[] }
interface Progress { phase: "idle" | "downloading" | "done" | "error"; message: string; bytesDone: number; bytesTotal: number }

const gb = (b: number) => b < 1024 ** 3 ? Math.round(b / 1024 ** 2) + " MB" : (b / 1024 ** 3).toFixed(b < 10 * 1024 ** 3 ? 1 : 0) + " GB"
const ctxText = (n: number) => n >= 1024 ? `${Math.round(n / 1024)}K` : String(n)

async function json<T>(path: string, init?: RequestInit): Promise<T> {
    const r = await apiFetch(path, init)
    const body = await r.json().catch(() => ({}))
    if (!r.ok) throw new Error((body as { error?: string }).error ?? `Request failed (${r.status})`)
    return body as T
}

export default function SetupWizard({ startStep, existingUser, onFinished }: Props) {
    const [step, setStep] = useState<"account" | "model" | "download" | "done">(startStep)
    const [user, setUser] = useState<AuthUser | null>(existingUser)
    const tokenRef = useRef<string | null>(null)

    return (
        <div style={overlay}>
            <div style={card}>
                <div style={logoRow}><img src="/images/logo-black.png?v=2" alt="A·R·I" style={logoImg} /></div>
                <Dots step={step} />
                {step === "account" && <AccountStep onDone={(u, t) => { setToken(t); tokenRef.current = t; setUser(u); setStep("model") }} />}
                {step === "model" && <ModelStep onChosen={() => setStep("download")} onSkip={async () => { await apiFetch("/api/v1/admin/setup", { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ setupComplete: true }) }); setStep("done") }} />}
                {step === "download" && <DownloadStep onDone={() => setStep("done")} onBack={() => setStep("model")} />}
                {step === "done" && (
                    <div style={form}>
                        <h2 style={h2}>You're set up</h2>
                        <p style={p}>Next, a short tour will show you around: where to chat, your settings, and the control panel where you switch ARI's parts on.</p>
                        <button style={btn(false)} onClick={() => onFinished(user!, tokenRef.current)}>Start the tour</button>
                    </div>
                )}
            </div>
        </div>
    )
}

function Dots({ step }: { step: string }) {
    const idx = step === "account" ? 0 : step === "done" ? 2 : 1
    return (
        <div style={{ display: "flex", justifyContent: "center", gap: 8, marginBottom: 20 }}>
            {["Account", "Model", "Ready"].map((l, i) => (
                <span key={l} style={{ fontSize: 12, padding: "3px 10px", borderRadius: 12,
                    background: i === idx ? "var(--col-login-screen-line-227-colour)" : "transparent",
                    color: i === idx ? "var(--col-login-screen-line-228-text)" : "var(--col-login-screen-line-190-text)", opacity: i <= idx ? 1 : 0.5 }}>{l}</span>
            ))}
        </div>
    )
}

function AccountStep({ onDone }: { onDone: (u: AuthUser, token: string) => void }) {
    const [token, setTok] = useState(""); const [username, setUsername] = useState("")
    const [password, setPassword] = useState(""); const [confirm, setConfirm] = useState("")
    const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false)

    async function submit(e: React.FormEvent) {
        e.preventDefault()
        if (password !== confirm) { setError("Passwords do not match."); return }
        setBusy(true); setError(null)
        try {
            const r = await json<{ token: string; role: string; displayName: string }>("/api/v1/setup/account", {
                method: "POST", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ token, username, password }),
            })
            onDone({ id: 0, username: username.trim(), role: r.role, displayName: r.displayName, mustChangePassword: false }, r.token)
        } catch (err) { setError(err instanceof Error ? err.message : "Failed") } finally { setBusy(false) }
    }

    return (
        <form onSubmit={submit} style={form}>
            <h2 style={h2}>Welcome to A·R·I</h2>
            <p style={p}>Create the admin account. The <b>Setup Token</b> is printed in ARI's log — in Docker or Portainer, open the container's logs and look for the boxed token.</p>
            <input style={input} placeholder="Setup Token (XXXX-XXXX-XXXX)" value={token} onChange={e => setTok(e.target.value)} autoFocus disabled={busy} />
            <input style={input} placeholder="Choose a username" autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} disabled={busy} />
            <input style={input} type="password" placeholder="Choose a password (8+ characters)" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} disabled={busy} />
            <input style={input} type="password" placeholder="Confirm password" autoComplete="new-password" value={confirm} onChange={e => setConfirm(e.target.value)} disabled={busy} />
            {error && <p style={errorStyle}>{error}</p>}
            <button type="submit" style={btn(busy)} disabled={busy || !token || !username || !password}>{busy ? "Creating…" : "Create account"}</button>
        </form>
    )
}

function ModelStep({ onChosen, onSkip }: { onChosen: () => void; onSkip: () => void }) {
    const [hw, setHw] = useState<Hardware | null>(null)
    const [url, setUrl] = useState("")
    const [res, setRes] = useState<Inspect | null>(null)
    const [sel, setSel] = useState<ModelFile | null>(null)
    const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false)

    useEffect(() => { json<Hardware>("/api/v1/admin/system/hardware").then(setHw).catch(() => {}) }, [])

    async function inspect(e: React.FormEvent) {
        e.preventDefault(); setBusy(true); setError(null); setRes(null); setSel(null)
        try {
            const r = await json<Inspect>("/api/v1/admin/setup/inspect", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ url }) })
            setRes(r)
            const pick = (q: string) => r.files.find(f => f.fit === "fits" && f.quant === q)
            setSel(pick("Q4_K_M") ?? pick("Q5_K_M") ?? pick("Q4_K_S") ?? [...r.files].reverse().find(f => f.fit === "fits") ?? null)
        } catch (err) { setError(err instanceof Error ? err.message : "Failed") } finally { setBusy(false) }
    }

    async function choose() {
        if (!sel || !res) return
        setBusy(true); setError(null)
        try {
            await json("/api/v1/admin/setup/model", { method: "POST", headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ repo: res.repo, file: sel.path, downloadUrl: sel.downloadUrl, sizeBytes: sel.sizeBytes, contextSize: Math.min(sel.maxContext, 32768) }) })
            onChosen()
        } catch (err) { setError(err instanceof Error ? err.message : "Failed"); setBusy(false) }
    }

    return (
        <div style={form}>
            <h2 style={h2}>Pick a model</h2>
            {hw && (
                <div style={memBox}>
                    <div><b>{gb(hw.usableBytes)}</b> usable for AI</div>
                    <div style={{ fontSize: 12, opacity: 0.75 }}>{gb(hw.totalBytes)} {hw.unified ? "memory" : "VRAM"} detected{hw.gpu ? ` · ${hw.gpu.name}` : ""}</div>
                    <div style={{ fontSize: 12, opacity: 0.75, marginTop: 4 }}>{hw.note}</div>
                </div>
            )}
            <form onSubmit={inspect} style={{ display: "flex", gap: 8 }}>
                <input style={{ ...input, margin: 0, flex: 1 }} placeholder="Paste a Hugging Face link (GGUF model)" value={url} onChange={e => setUrl(e.target.value)} disabled={busy} />
                <button type="submit" style={{ ...btn(busy), width: "auto", padding: "0 16px" }} disabled={busy || !url.trim()}>{busy && !res ? "…" : "Check"}</button>
            </form>
            {error && <p style={errorStyle}>{error}</p>}
            {res && (
                <>
                    <p style={{ ...p, margin: "12px 0 6px", fontSize: 12 }}>{res.repo} — {res.files.length} file(s){res.kvExact ? "" : " · memory use estimated"}</p>
                    <div style={{ maxHeight: 240, overflowY: "auto", border: "1px solid var(--col-login-screen-line-190-text)", borderRadius: 8 }}>
                        {res.files.map(f => (
                            <label key={f.path} style={{ display: "flex", alignItems: "center", gap: 10, padding: "8px 10px", cursor: f.fit === "no" ? "not-allowed" : "pointer", opacity: f.fit === "no" ? 0.55 : 1, borderBottom: "1px solid rgba(128,128,128,.15)" }}>
                                <input type="radio" name="gguf" disabled={f.fit === "no"} checked={sel?.path === f.path} onChange={() => setSel(f)} />
                                <span style={{ flex: 1, fontSize: 13, wordBreak: "break-all" }}>{f.name}{/^(IQ1|IQ2|Q2)/.test(f.quant) && <em style={{ display: "block", fontSize: 11, opacity: 0.7 }}>very small quant: fast, but replies are noticeably worse</em>}</span>
                                <span style={{ fontSize: 12 }}>{gb(f.sizeBytes)}</span>
                                <span style={badge(f.fit)}>{f.fit === "fits" ? `fits · ${ctxText(f.maxContext)} ctx` : f.fit === "tight" ? `tight · ${ctxText(f.maxContext)} ctx` : "won't fit"}</span>
                            </label>
                        ))}
                    </div>
                    <button style={{ ...btn(busy), marginTop: 14 }} disabled={!sel || busy} onClick={choose}>
                        {sel ? `Download ${gb(sel.sizeBytes)} and continue` : "Choose a model that fits"}
                    </button>
                </>
            )}
            <button type="button" style={backBtn} onClick={onSkip}>Skip — I'll add a model later</button>
        </div>
    )
}

function DownloadStep({ onDone, onBack }: { onDone: () => void; onBack: () => void }) {
    const [pr, setPr] = useState<Progress | null>(null)
    useEffect(() => {
        const t = setInterval(async () => {
            try {
                const p = await json<Progress>("/api/v1/admin/setup/progress")
                setPr(p)
                if (p.phase === "done") { clearInterval(t); onDone() }
                if (p.phase === "error") clearInterval(t)
            } catch { /* keep polling */ }
        }, 1000)
        return () => clearInterval(t)
    }, [onDone])
    const pct = pr && pr.bytesTotal > 0 ? Math.min(100, Math.round(pr.bytesDone / pr.bytesTotal * 100)) : 0
    return (
        <div style={form}>
            <h2 style={h2}>Downloading your model</h2>
            <div style={{ height: 10, background: "rgba(128,128,128,.25)", borderRadius: 6, overflow: "hidden" }}>
                <div style={{ height: "100%", width: `${pct}%`, background: "var(--col-login-screen-line-227-colour)", transition: "width .4s" }} />
            </div>
            <p style={{ ...p, marginTop: 10 }}>{pr ? `${gb(pr.bytesDone)} of ${pr.bytesTotal ? gb(pr.bytesTotal) : "?"} (${pct}%)` : "Starting…"}</p>
            {pr?.phase === "error" && (<><p style={errorStyle}>{pr.message}</p><button style={btn(false)} onClick={onBack}>Back</button></>)}
        </div>
    )
}

const overlay: React.CSSProperties = { position: "fixed", inset: 0, display: "flex", alignItems: "center", justifyContent: "center", background: "var(--col-login-screen-line-158-background)", zIndex: 9999, overflowY: "auto" }
const card: React.CSSProperties = { background: "var(--col-login-screen-line-163-background)", borderRadius: 16, boxShadow: "0 4px 32px var(--col-login-screen-line-165-shadow)", padding: "32px 36px", width: 520, maxWidth: "calc(100vw - 32px)", margin: "16px 0" }
const logoRow: React.CSSProperties = { display: "flex", justifyContent: "center", marginBottom: 16, background: "var(--col-login-screen-line-176-background)", borderRadius: 10, padding: "16px 24px" }
const logoImg: React.CSSProperties = { height: 36, width: "auto", maxWidth: 220, objectFit: "contain" }
const form: React.CSSProperties = { display: "flex", flexDirection: "column" }
const h2: React.CSSProperties = { margin: "0 0 8px", fontSize: 20, textAlign: "center", color: "var(--col-login-screen-line-190-text)" }
const p: React.CSSProperties = { margin: "0 0 16px", fontSize: 13, lineHeight: 1.5, color: "var(--col-login-screen-line-190-text)", textAlign: "center" }
const input: React.CSSProperties = { padding: "10px 12px", borderRadius: 8, border: "1.5px solid var(--col-login-screen-line-211-border)", background: "var(--col-login-screen-line-214-background)", color: "var(--col-login-screen-line-215-text)", fontSize: 14, marginBottom: 10, width: "100%", boxSizing: "border-box", outline: "none" }
const memBox: React.CSSProperties = { padding: "10px 12px", borderRadius: 8, background: "rgba(128,128,128,.12)", marginBottom: 14, color: "var(--col-login-screen-line-190-text)", fontSize: 14 }
const errorStyle: React.CSSProperties = { margin: "8px 0 0", padding: "10px 12px", borderRadius: 6, background: "var(--col-login-screen-line-251-background)", border: "1px solid var(--col-login-screen-line-252-border)", color: "var(--col-login-screen-line-253-text)", fontSize: 13 }
const btn = (disabled: boolean): React.CSSProperties => ({ padding: "11px 0", borderRadius: 8, border: "none", fontSize: 14, fontWeight: 600, cursor: disabled ? "not-allowed" : "pointer", opacity: disabled ? 0.6 : 1, background: disabled ? "var(--col-login-screen-line-227-background)" : "var(--col-login-screen-line-227-colour)", color: "var(--col-login-screen-line-228-text)", width: "100%" })
const backBtn: React.CSSProperties = { marginTop: 12, background: "transparent", border: "1.5px solid var(--col-login-screen-line-240-border)", borderRadius: 8, padding: "10px 0", cursor: "pointer", fontSize: 13, color: "var(--col-login-screen-line-242-text)" }
const badge = (fit: string): React.CSSProperties => ({ fontSize: 11, padding: "2px 8px", borderRadius: 10, whiteSpace: "nowrap", color: "#fff", background: fit === "fits" ? "#2e8b57" : fit === "tight" ? "#c98a00" : "#b03030" })
