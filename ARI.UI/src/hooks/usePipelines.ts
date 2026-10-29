import { useState, useEffect } from "react"

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
