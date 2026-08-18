import { useRef, useEffect, useState } from "react"
import { apiFetch } from "../auth"

// The one global reasoning-effort dial, surfaced on the composer's hint row. Only renders when the
// active model supports the reasoning_effort field. Three honest stops (Low / Medium / High) matching
// what the model exposes; the chosen step is universal across every agent and persists server-side.
// See Documentation/Server/ARI.LLM/Reasoning-Effort.

const STOP_LABELS = ["Low", "Medium", "High"]
const STOP_BLURBS = [
    "Concludes thoughts quickly",
    "Allows longer, more complex reasoning",
    "Use for the most complex tasks — allows a nearly unbounded chain of thought, at the expense of response time",
]

interface Props {
    serverReady: boolean
    // True in Code mode: gate on the Coder agent's model instead of Dialogue's.
    codeMode:    boolean
}

export default function ThinkingEffort({ serverReady, codeMode }: Props) {
    const [supported, setSupported] = useState(false)
    const [step, setStep]           = useState(0)
    const [open, setOpen]           = useState(false)
    const wrapRef = useRef<HTMLDivElement>(null)

    // The step is a global setting; support is per-pipeline — the dial shows only when the model that will
    // actually answer supports reasoning_effort (Dialogue's model in Default mode, Coder's in Code mode).
    // Re-checked when the server comes up / swaps model (serverReady) or the mode changes (codeMode).
    useEffect(() => {
        let cancelled = false
        async function load() {
            try {
                const res = await apiFetch("/models/reasoning-effort")
                if (cancelled) return
                const effort = await res.json()
                setSupported(codeMode ? effort?.support?.coder === true : effort?.support?.dialogue === true)
                if (typeof effort?.step === "number") setStep(effort.step)
            } catch {
                if (!cancelled) setSupported(false)
            }
        }
        load()
        return () => { cancelled = true }
    }, [serverReady, codeMode])

    // Close the popover on any outside click.
    useEffect(() => {
        if (!open) return
        function onDown(e: MouseEvent) {
            if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false)
        }
        document.addEventListener("mousedown", onDown)
        return () => document.removeEventListener("mousedown", onDown)
    }, [open])

    function changeStep(next: number) {
        setStep(next)
        apiFetch("/models/reasoning-effort", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ step: next }),
        }).catch(() => { /* best-effort; UI already reflects the choice */ })
    }

    if (!supported) return null

    return (
        <div id="thinking-effort" ref={wrapRef}>
            <button
                id="btn-thinking-effort"
                className={open ? "active" : ""}
                title={`Thinking effort — ${STOP_LABELS[step]}`}
                onClick={() => setOpen(o => !o)}
            >
                {/* brain icon */}
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M9.5 2A2.5 2.5 0 0 0 7 4.5v.5a3 3 0 0 0-2 5.24V15a3 3 0 0 0 3 3 2.5 2.5 0 0 0 5 0V4.5A2.5 2.5 0 0 0 9.5 2Z"/>
                    <path d="M14.5 2A2.5 2.5 0 0 1 17 4.5v.5a3 3 0 0 1 2 5.24V15a3 3 0 0 1-3 3"/>
                </svg>
            </button>
            {open && (
                <div id="thinking-effort-popup">
                    <div className="te-head">
                        <span className="te-head-label">Effort</span>
                        <span className="te-head-value">{STOP_LABELS[step]}</span>
                    </div>
                    <div className="te-ends">
                        <span>Faster</span>
                        <span>Smarter</span>
                    </div>
                    <input
                        className="te-slider"
                        type="range"
                        min={0}
                        max={STOP_LABELS.length - 1}
                        step={1}
                        value={step}
                        onChange={e => changeStep(parseInt(e.target.value, 10))}
                    />
                    <p className="te-blurb">{STOP_BLURBS[step]}</p>
                </div>
            )}
        </div>
    )
}
