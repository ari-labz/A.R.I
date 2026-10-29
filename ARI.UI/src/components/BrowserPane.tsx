import { useEffect, useRef, useState } from "react"
import { apiFetch } from "../auth"

interface Status { open: boolean; waiting?: boolean; prompt?: string | null; tabs?: string[]; active?: number }

interface Props { activeThread: string | null }

// The conversation's browser, shown only while Ari has a page open. A picture of the page refreshes about once a second; clicking,
// typing and scrolling over it drive the real page, so the person can log in for her and press Done.
export default function BrowserPane({ activeThread }: Props) {
    const [status, setStatus] = useState<Status>({ open: false })
    const [shot, setShot] = useState<string | null>(null)
    const [hidden, setHidden] = useState(false)
    const [address, setAddress] = useState("")
    const imgRef = useRef<HTMLImageElement>(null)
    const wasWaiting = useRef(false)

    const base = activeThread ? `/api/v1/threads/${activeThread}/browser` : null

    useEffect(() => { setStatus({ open: false }); setShot(null); setHidden(false) }, [activeThread])

    // Is there a page? Cheap, so check every couple of seconds.
    useEffect(() => {
        if (!base) return
        let stop = false
        const tick = async () => {
            try {
                const r = await apiFetch(base)
                if (!stop && r.ok) setStatus(await r.json())
                else if (!stop) setStatus({ open: false })
            } catch { /* server busy */ }
        }
        tick()
        const t = setInterval(tick, 2000)
        return () => { stop = true; clearInterval(t) }
    }, [base])

    // When she asks for the person, bring the pane back even if it was tucked away.
    useEffect(() => {
        if (status.waiting && !wasWaiting.current) setHidden(false)
        wasWaiting.current = !!status.waiting
    }, [status.waiting])

    // The picture.
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

    if (!base || !status.open) return null
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

    if (hidden) return (
        <button id="browser-pane-tab" onClick={() => setHidden(false)} title="Show Ari's browser">
            Browser{status.waiting ? " — needs you" : ""}
        </button>
    )

    return (
        <div id="browser-pane" className={status.waiting ? "waiting" : ""}>
            <div className="bp-bar">
                <span className="bp-url" title={url}>{url || "Ari's browser"}</span>
                <form onSubmit={e => { e.preventDefault(); if (address.trim()) { send({ kind: "navigate", text: address.trim() }); setAddress("") } }}>
                    <input value={address} onChange={e => setAddress(e.target.value)} placeholder="Go to…" spellCheck={false} />
                </form>
                <button onClick={() => setHidden(true)} title="Tuck away">–</button>
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
