import { useState, useEffect } from "react"

// The pipelines a thread can run on, fetched from GET /pipelines (lowercased enum names).
// Data-driven so a new backend ThreadPipeline value surfaces in the selector without a UI change.
// Falls back to the known set if the request fails so the selector is never empty.
const FALLBACK = ["dialogue", "code", "speech"]

export function usePipelines(): string[] {
    const [pipelines, setPipelines] = useState<string[]>(FALLBACK)

    useEffect(() => {
        let alive = true
        fetch("/pipelines")
            .then(r => (r.ok ? r.json() : null))
            .then((list: string[] | null) => { if (alive && Array.isArray(list) && list.length) setPipelines(list) })
            .catch(() => { /* keep fallback */ })
        return () => { alive = false }
    }, [])

    return pipelines
}
