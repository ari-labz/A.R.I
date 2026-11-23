import { useEffect, useRef, useState } from "react"

export interface PersonaOption { id: string; name: string; description: string }

interface Props {
    personas: PersonaOption[]
    current:  string
    onChoose: (id: string) => void
}

/// Which persona this conversation speaks in. Shown on the composer's hint row only when there is more than one to pick from;
/// the choice applies from the next reply, and earlier replies stay as they were.
export default function PersonaPicker({ personas, current, onChoose }: Props) {
    const [open, setOpen] = useState(false)
    const wrapRef = useRef<HTMLDivElement>(null)

    useEffect(() => {
        if (!open) return
        const onDown = (e: MouseEvent) => { if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false) }
        document.addEventListener("mousedown", onDown)
        return () => document.removeEventListener("mousedown", onDown)
    }, [open])

    if (personas.length < 2) return null
    const active = personas.find(p => p.id === current) ?? personas[0]

    return (
        <div id="persona-picker" ref={wrapRef}>
            <button id="btn-persona" className={open ? "active" : ""} title={`Persona: ${active.name}`} onClick={() => setOpen(o => !o)}>
                {active.name}
            </button>
            {open && (
                <div id="persona-popup">
                    {personas.map(p => (
                        <button key={p.id} className={p.id === active.id ? "selected" : ""} onClick={() => { onChoose(p.id); setOpen(false) }}>
                            <span className="persona-name">{p.name}</span>
                            {p.description && <span className="persona-desc">{p.description}</span>}
                        </button>
                    ))}
                </div>
            )}
        </div>
    )
}
