import { useState, useEffect } from "react"
import { apiFetch } from "../auth"

export interface PipelineOption { id: string; label: string }

// The pipelines a thread can run on, fetched from GET /pipelines as { id, label }: the built-in modes plus every pipeline
// made in the control panel, so a new one shows up in the selector without a UI change. Falls back to the built-ins if
// the request fails so the selector is never empty.
const FALLBACK: PipelineOption[] = [
    { id: "dialogue", label: "Default" },
    { id: "code",     label: "Code" },
]

export function usePipelines(): PipelineOption[] {
    const [pipelines, setPipelines] = useState<PipelineOption[]>(FALLBACK)

    useEffect(() => {
        let alive = true
        fetch("/api/v1/pipelines")
            .then(r => (r.ok ? r.json() : null))
            .then((list: PipelineOption[] | null) => { if (alive && Array.isArray(list) && list.length) setPipelines(list) })
            .catch(() => { /* keep fallback */ })
        return () => { alive = false }
    }, [])

    return pipelines
}

// Whether the voice module is running: the server only lists the "speech" pipeline while it is. Asked once per page
// and shared, since every reply's speaker button wants to know.
let voiceRunning: Promise<boolean> | null = null
export function useVoiceAvailable(): boolean {
    const [available, setAvailable] = useState(false)
    useEffect(() => {
        let alive = true
        voiceRunning ??= apiFetch("/api/v1/pipelines")
            .then(r => (r.ok ? r.json() : []))
            .then((list: PipelineOption[]) => Array.isArray(list) && list.some(p => p.id === "speech"))
            .catch(() => false)
        voiceRunning.then(v => { if (alive) setAvailable(v) })
        return () => { alive = false }
    }, [])
    return available
}
