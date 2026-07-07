import { useEffect, useRef, forwardRef, useImperativeHandle } from "react"
import { createOrbRenderer, type OrbRenderer, type OrbStateName } from "ari-orb"

export type OrbState = OrbStateName

// Imperative handle so later work (#91 audio → #94 amplitude) can pulse the orb per syllable while speaking.
export interface OrbHandle {
    pulse: (amount?: number) => void
    setState: (state: OrbState) => void
}

interface Props {
    state?: OrbState
    caption?: string | null   // overrides the state label (e.g. live transcript)
}

const LABELS: Record<OrbState, string> = {
    idle:        "Tap to speak",
    listening:   "Listening…",
    thinking:    "Thinking…",
    speaking:    "Speaking…",
    interrupted: "…",
}

// Orb overlay shell (issue #92) — a bespoke WebGL orb in ARI's palette. State machine (#93) and
// mic/output amplitude (#94) drive `state`/`pulse` from Listener events later.
const Orb = forwardRef<OrbHandle, Props>(function Orb({ state = "idle", caption }, ref) {
    const canvasRef = useRef<HTMLCanvasElement>(null)
    const rendererRef = useRef<OrbRenderer | null>(null)

    useEffect(() => {
        if (!canvasRef.current) return
        const r = createOrbRenderer(canvasRef.current)
        rendererRef.current = r
        const ro = new ResizeObserver(() => r.resize())
        if (canvasRef.current.parentElement) ro.observe(canvasRef.current.parentElement)
        window.addEventListener("resize", r.resize)
        return () => {
            window.removeEventListener("resize", r.resize)
            ro.disconnect()
            r.dispose()
            rendererRef.current = null
        }
    }, [])

    useEffect(() => { rendererRef.current?.setState(state) }, [state])

    useImperativeHandle(ref, () => ({
        pulse: amount => rendererRef.current?.pulse(amount),
        setState: s => rendererRef.current?.setState(s),
    }), [])

    return (
        <div className={`orb-stage orb-${state}`}>
            <div className="orb" onClick={() => rendererRef.current?.pulse(1.4)}>
                <canvas ref={canvasRef} className="orb-canvas" />
            </div>
            <p className="orb-label">{caption || LABELS[state]}</p>
        </div>
    )
})

export default Orb
