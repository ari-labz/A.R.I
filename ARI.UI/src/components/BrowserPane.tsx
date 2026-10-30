import { useEffect, useRef, useState } from "react"
import { apiFetch } from "../auth"
import type { BrowserSession } from "../hooks/useBrowserSession"

// Ari's browser for this conversation. It has three parts: a button in the header (BrowserButton) that shows or hides it, the pane itself
// beside the chat (BrowserPane), and a line above the input when she is waiting for the person (BrowserNeedsYou). A picture of the page
// refreshes about once a second; clicking, typing and scrolling over it drive the real page, so the person can log in for her and press Done.

export function BrowserButton({ session }: { session: BrowserSession }) {
    const { status, hidden } = session
    if (!status.open) return null
    const tabs = status.tabs?.length ?? 1
    return (
        <button id="browser-btn" className={`${status.waiting ? "waiting" : ""} ${hidden ? "" : "on"}`.trim()} onClick={session.toggle}
                title={hidden ? "Show Ari's browser" : "Hide Ari's browser"}>
            <span className="bb-dot" aria-hidden="true" />
            <span>Browser</span>
            <span className="bb-sub">· {status.waiting ? "needs you" : `${tabs} tab${tabs === 1 ? "" : "s"}`}</span>
        </button>
    )
}

export function BrowserNeedsYou({ session }: { session: BrowserSession }) {
    const { status, hidden } = session
    if (!status.open || !status.waiting || !hidden) return null
    return (
        <div id="browser-need">
            <span>{status.prompt || "Ari needs you to do something in the browser."}</span>
            <button onClick={session.show}>Open browser</button>
        </div>
    )
}

export default function BrowserPane({ session }: { session: BrowserSession }) {
    const { base, status, hidden } = session
    const [shot, setShot] = useState<string | null>(null)
    const [address, setAddress] = useState("")
    const imgRef = useRef<HTMLImageElement>(null)

    useEffect(() => { setShot(null) }, [base])

    // The picture, only while the pane is on screen.
    useEffect(() => {
        if (!base || !status.open || hidden) return
        let stop = false
        const grab = async () => {
            try {
                const r = await apiFetch(base + "/screen")
                if (!r.ok || stop) return
                const next = URL.createObjectURL(await r.blob())
                setShot(prev => { if (prev) URL.revokeObjectURL(prev); return next })
            } catch { /* try again */ }
        }
        grab()
        const t = setInterval(grab, status.waiting ? 700 : 1200)
        return () => { stop = true; clearInterval(t) }
    }, [base, status.open, status.waiting, hidden])

    if (!base || !status.open || hidden) return null
    const url = status.tabs?.[status.active ?? 0] ?? ""

    const send = (body: object) => apiFetch(base + "/input", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }).catch(() => {})

    function point(e: { clientX: number; clientY: number }) {
        const img = imgRef.current!, r = img.getBoundingClientRect()
        return { x: (e.clientX - r.left) * (img.naturalWidth / r.width), y: (e.clientY - r.top) * (img.naturalHeight / r.height) }
    }

    function onKeyDown(e: React.KeyboardEvent) {
        if (e.metaKey || e.ctrlKey) return
        if (e.key.length === 1) { e.preventDefault(); send({ kind: "type", text: e.key }) }
        else if (["Enter", "Backspace", "Tab", "Escape", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Delete"].includes(e.key)) { e.preventDefault(); send({ kind: "key", text: e.key }) }
    }

    function onPaste(e: React.ClipboardEvent) {
        const text = e.clipboardData.getData("text")
        if (text) { e.preventDefault(); send({ kind: "type", text }) }
    }

    return (
        <div id="browser-pane" className={status.waiting ? "waiting" : ""}>
            <div className="bp-bar">
                <span className="bp-url" title={url}>{url || "Ari's browser"}</span>
                <form onSubmit={e => { e.preventDefault(); if (address.trim()) { send({ kind: "navigate", text: address.trim() }); setAddress("") } }}>
                    <input value={address} onChange={e => setAddress(e.target.value)} placeholder="Go to…" spellCheck={false} />
                </form>
                <button onClick={() => session.setHidden(true)} title="Hide (the Browser button in the header brings it back)">Hide ›</button>
                <button onClick={() => apiFetch(base + "/close", { method: "POST" })} title="Close the browser">×</button>
            </div>
            {status.waiting && (
                <div className="bp-ask">
                    <span>{status.prompt || "Ari needs you to do something in the page."}</span>
                    <button onClick={() => apiFetch(base + "/done", { method: "POST" })}>Done</button>
                </div>
            )}
            <div className="bp-view" tabIndex={0} onKeyDown={onKeyDown} onPaste={onPaste}
                 onWheel={e => { const p = point(e); send({ kind: "scroll", x: p.x, y: p.y, deltaY: e.deltaY }) }}>
                {shot ? <img ref={imgRef} src={shot} alt="Ari's browser" draggable={false}
                            onClick={e => { (e.currentTarget.parentElement as HTMLElement).focus(); const p = point(e); send({ kind: "click", x: p.x, y: p.y }) }} />
                      : <div className="bp-loading">Loading…</div>}
            </div>
        </div>
    )
}
