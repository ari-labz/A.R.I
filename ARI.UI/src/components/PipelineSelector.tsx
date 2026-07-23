import { useRef, useState, useEffect, useLayoutEffect } from "react"

interface Props {
    pipelines:   string[]
    // null = Default (Dialogue unless a bound Repository project forces Code — no explicit pin).
    // A pipeline id pins the thread outright, same idea as switching from Claude to Claude Code.
    value:       string | null
    onChange:    (id: string | null) => void
    orientation?: "horizontal" | "vertical"
    disabled?:   boolean
}

// Display label per known pipeline id. Unknown ids fall back to a capitalized name so a future backend
// pipeline still renders without a change here. "dialogue" has no button of its own — it's what
// "Default" resolves to, not a separate explicit pin (mirrors Claude vs. Claude Code: you don't select
// "Claude" mode, you're just in it unless you switch to Code).
const LABELS: Record<string, string> = {
    code:   "Code",
    speech: "Talk",
}

function labelFor(id: string) {
    return LABELS[id] ?? id.charAt(0).toUpperCase() + id.slice(1)
}

export default function PipelineSelector({ pipelines, value, onChange, orientation = "horizontal", disabled = false }: Props) {
    const options: Array<{ id: string | null; label: string }> = [
        { id: null, label: "Default" },
        ...pipelines.filter(id => id !== "dialogue").map(id => ({ id, label: labelFor(id) })),
    ]

    const activeIndex = Math.max(0, options.findIndex(o => o.id === value))
    const btnRefs = useRef<Array<HTMLButtonElement | null>>([])
    const mounted = useRef(false)
    const [thumb, setThumb] = useState<{ left: number; width: number } | null>(null)
    const [instant, setInstant] = useState(true)  // skip the transition on the very first paint
    const [animating, setAnimating] = useState(false)

    // Position the sliding pill under the active segment (measured — labels vary in width).
    useLayoutEffect(() => {
        const btn = btnRefs.current[activeIndex]
        if (!btn) return
        setThumb({ left: btn.offsetLeft, width: btn.offsetWidth })
    }, [activeIndex, pipelines.length, orientation])

    // Hide the separators while the pill is gliding so no divider shows through it.
    useEffect(() => {
        if (!mounted.current) return
        setAnimating(true)
        const t = setTimeout(() => setAnimating(false), 220)
        return () => clearTimeout(t)
    }, [activeIndex])

    // After the first paint the pill is already under the active segment; enable transitions
    // so subsequent selections glide instead of the initial placement animating in.
    useEffect(() => { mounted.current = true; setInstant(false) }, [])

    const isH = orientation === "horizontal"

    return (
        <div className={`pipeline-selector ${orientation}${disabled ? " disabled" : ""}${animating ? " animating" : ""}`} role="radiogroup" aria-label="Pipeline">
            {isH && thumb && (
                <span
                    className={`pipeline-thumb${instant ? " instant" : ""}`}
                    style={{ transform: `translateX(${thumb.left}px)`, width: thumb.width }}
                />
            )}
            {options.map((o, i) => (
                <button
                    key={o.id ?? "default"}
                    ref={el => { btnRefs.current[i] = el }}
                    type="button"
                    className={`pipeline-option${value === o.id ? " active" : ""}`}
                    role="radio"
                    aria-checked={value === o.id}
                    title={o.id === null ? "Default — Dialogue, unless a bound Repository project switches to Code" : o.label}
                    disabled={disabled}
                    onClick={e => { e.stopPropagation(); onChange(o.id) }}
                >
                    {o.label}
                </button>
            ))}
        </div>
    )
}
