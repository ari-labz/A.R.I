import { useRef, useState, useEffect, useLayoutEffect } from "react"

interface Option<T extends string> { value: T; label: string }

interface Props<T extends string> {
    options:  Option<T>[]
    value:    T
    onChange: (v: T) => void
    disabled?: boolean
}

// Generic two-or-more-way iOS-style segmented control — same sliding-pill mechanics as
// PipelineSelector, but for plain string options (project type, storage backend, etc.)
// instead of pipeline ids specifically.
export default function SegmentedControl<T extends string>({ options, value, onChange, disabled = false }: Props<T>) {
    const activeIndex = Math.max(0, options.findIndex(o => o.value === value))
    const btnRefs = useRef<Array<HTMLButtonElement | null>>([])
    const mounted = useRef(false)
    const [thumb, setThumb] = useState<{ left: number; width: number } | null>(null)
    const [instant, setInstant] = useState(true)
    const [animating, setAnimating] = useState(false)

    useLayoutEffect(() => {
        const btn = btnRefs.current[activeIndex]
        if (!btn) return
        setThumb({ left: btn.offsetLeft, width: btn.offsetWidth })
    }, [activeIndex, options.length])

    useEffect(() => {
        if (!mounted.current) return
        setAnimating(true)
        const t = setTimeout(() => setAnimating(false), 220)
        return () => clearTimeout(t)
    }, [activeIndex])

    useEffect(() => { mounted.current = true; setInstant(false) }, [])

    return (
        <div className={`pipeline-selector horizontal${disabled ? " disabled" : ""}${animating ? " animating" : ""}`} role="radiogroup">
            {thumb && (
                <span
                    className={`pipeline-thumb${instant ? " instant" : ""}`}
                    style={{ transform: `translateX(${thumb.left}px)`, width: thumb.width }}
                />
            )}
            {options.map((o, i) => (
                <button
                    key={o.value}
                    ref={el => { btnRefs.current[i] = el }}
                    type="button"
                    className={`pipeline-option${value === o.value ? " active" : ""}`}
                    role="radio"
                    aria-checked={value === o.value}
                    disabled={disabled}
                    onClick={e => { e.stopPropagation(); onChange(o.value) }}
                >
                    {o.label}
                </button>
            ))}
        </div>
    )
}
