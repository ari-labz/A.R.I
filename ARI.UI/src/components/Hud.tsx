import { useEffect, useLayoutEffect, useRef, useState } from "react"
import { createHudFace, type HudFace, type HudStateName } from "ari-hud"

export type HudState = HudStateName

/** Something Ari put on screen during the call: one of her visuals (show_visual) or an image. */
export interface HudWindow {
    id:    string
    title: string
    kind:  "visual" | "image"
    src:   string
}

interface Props {
    state:     HudState
    caption?:  string | null
    windows:   HudWindow[]
    onCloseWindow: (id: string) => void
    inCall:    boolean
    muted:     boolean
    onMute:    () => void
    onHangUp:  () => void
    onCall:    () => void
    levels?:   { current: number[] }   // her voice, per band (see useListener)
    mic?:      { current: number }     // the user's mic loudness
}

const LABELS: Record<HudState, string> = {
    idle:      "",
    listening: "Listening…",
    thinking:  "Thinking…",
    speaking:  "",
}

const GAP = 12
const METER_BARS = 12

// Ari's voice-mode screen: her layered face fills it; windows she opens tile the space without overlapping and
// her face eases into the bottom-right corner; call controls (mute, hang up) sit along the bottom.
export default function Hud({ state, caption, windows, onCloseWindow, inCall, muted, onMute, onHangUp, onCall, levels, mic }: Props) {
    const canvasRef = useRef<HTMLCanvasElement>(null)
    const faceRef = useRef<HudFace | null>(null)
    const winsRef = useRef<HTMLDivElement>(null)
    const meterRef = useRef<HTMLDivElement>(null)
    const [closing, setClosing] = useState<Set<string>>(new Set())
    const [entering, setEntering] = useState<Set<string>>(new Set())
    const seen = useRef<Set<string>>(new Set())
    const docked = windows.length > 0

    useEffect(() => {
        if (!canvasRef.current) return
        const face = createHudFace(canvasRef.current, { draggable: true })
        faceRef.current = face
        // The face resizes as it docks and undocks, so watch the canvas itself.
        const ro = new ResizeObserver(() => face.resize())
        ro.observe(canvasRef.current)
        // Feed her voice loudness and the mic meter once per frame.
        let raf = 0
        const tick = () => {
            const bands = levels?.current
            face.setLevel(bands && bands.length ? bands.reduce((a, b) => a + b, 0) / bands.length * 1.6 : 0)
            const bars = meterRef.current?.children
            if (bars) {
                const m = mic?.current ?? 0, t = performance.now()
                for (let i = 0; i < bars.length; i++) {
                    const on = m * (0.5 + 0.5 * Math.sin(i * 1.3 + t / 90))
                    ;(bars[i] as HTMLElement).style.height = `${3 + on * 15}px`
                    ;(bars[i] as HTMLElement).classList.toggle("on", on > 0.2)
                }
            }
            raf = requestAnimationFrame(tick)
        }
        raf = requestAnimationFrame(tick)
        return () => { cancelAnimationFrame(raf); ro.disconnect(); face.dispose(); faceRef.current = null }
    }, [levels, mic])

    useEffect(() => { faceRef.current?.setState(state) }, [state])

    // New windows start small at the face's corner, then grow into their tile.
    useLayoutEffect(() => {
        const fresh = windows.filter(w => !seen.current.has(w.id)).map(w => w.id)
        if (!fresh.length) return
        fresh.forEach(id => seen.current.add(id))
        setEntering(prev => new Set([...prev, ...fresh]))
        const raf = requestAnimationFrame(() => requestAnimationFrame(() =>
            setEntering(prev => { const next = new Set(prev); fresh.forEach(id => next.delete(id)); return next })))
        return () => cancelAnimationFrame(raf)
    }, [windows])

    // Tile the open windows: one row of up to two, then two or three columns, the last row stretching to fill.
    const [, setTick] = useState(0)
    useEffect(() => {
        const el = winsRef.current; if (!el) return
        const ro = new ResizeObserver(() => setTick(t => t + 1))
        ro.observe(el)
        return () => ro.disconnect()
    }, [])
    const W = winsRef.current?.clientWidth ?? 0, H = winsRef.current?.clientHeight ?? 0
    const shown = windows.filter(w => !closing.has(w.id))
    const n = shown.length
    const cols = W < 560 ? 1 : n <= 2 ? Math.max(1, n) : n <= 4 ? 2 : 3
    const rows = Math.max(1, Math.ceil(n / cols))
    const cellH = (H - GAP * (rows - 1)) / rows
    const tile = (id: string): React.CSSProperties => {
        const i = shown.findIndex(w => w.id === id)
        if (i < 0 || entering.has(id)) return { left: W * .55, top: H * .55, width: W * .45, height: H * .45 }
        const r = Math.floor(i / cols), inRow = r === rows - 1 ? n - r * cols : cols, c = i - r * cols
        const cellW = (W - GAP * (inRow - 1)) / inRow
        return { left: c * (cellW + GAP), top: r * (cellH + GAP), width: cellW, height: cellH }
    }
    const close = (id: string) => {
        setClosing(prev => new Set(prev).add(id))
        setTimeout(() => { onCloseWindow(id); setClosing(prev => { const next = new Set(prev); next.delete(id); return next }) }, 420)
    }

    const label = caption || (inCall ? (muted ? "Muted" : LABELS[state]) : "")

    return (
        <div className={`hud-stage${docked ? " docked" : ""}`}>
            <div className="hud-wins" ref={winsRef}>
                {windows.map(w => (
                    <section key={w.id} className={`hud-win${entering.has(w.id) || closing.has(w.id) ? " hidden-state" : ""}`} style={tile(w.id)}>
                        <header>
                            <i />
                            <b title={w.title}>{w.title}</b>
                            <button aria-label={`Close ${w.title}`} onClick={() => close(w.id)}>×</button>
                        </header>
                        <div className="hud-win-body">
                            {w.kind === "visual"
                                ? <div className="ari-visual" data-visual-src={w.src} data-visual-name={w.title} />
                                : <img src={w.src} alt={w.title} />}
                        </div>
                    </section>
                ))}
            </div>

            <canvas ref={canvasRef} className="hud-face" role="img" aria-label="Ari" />

            <p className="hud-caption">{label}</p>

            <div className="hud-callbar">
                {inCall ? (
                    <>
                        <div className="hud-meter" ref={meterRef} aria-hidden="true">
                            {Array.from({ length: METER_BARS }, (_, i) => <i key={i} />)}
                        </div>
                        <button className={`hud-round${muted ? " active" : ""}`} aria-pressed={muted}
                                aria-label={muted ? "Unmute" : "Mute"} title={muted ? "Unmute" : "Mute"} onClick={onMute}>
                            <svg viewBox="0 0 24 24"><rect x="9" y="3" width="6" height="11" rx="3" /><path d="M5 11a7 7 0 0 0 14 0M12 18v3" />{muted && <path d="M3 3l18 18" />}</svg>
                        </button>
                        <button className="hud-round hud-hangup" aria-label="Hang up" title="Hang up" onClick={onHangUp}>
                            <svg viewBox="0 0 24 24"><path d="M3 14.5c5-4.7 13-4.7 18 0l-2.2 2.6-3.6-1.3v-2.4a10 10 0 0 0-6.4 0v2.4l-3.6 1.3z" /></svg>
                        </button>
                    </>
                ) : (
                    <button className="hud-call" onClick={onCall}>
                        <svg viewBox="0 0 24 24"><path d="M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2" /></svg>
                        Call Ari
                    </button>
                )}
            </div>
        </div>
    )
}
