import { useState, useEffect } from "react"
import { apiFetch } from "../auth"

// What the AI is called (Customisation → Branding in the control panel), from the public GET /branding. Asked once per
// page and shared; "A.R.I" until it arrives, or if it can't be read.
export const DEFAULT_ASSISTANT_NAME = "A.R.I"
let loading: Promise<string> | null = null

export function useAssistantName(): string {
    const [name, setName] = useState(DEFAULT_ASSISTANT_NAME)
    useEffect(() => {
        let alive = true
        loading ??= apiFetch("/api/v1/branding")
            .then(r => (r.ok ? r.json() : null))
            .then((b: { name?: string } | null) => b?.name?.trim() || DEFAULT_ASSISTANT_NAME)
            .catch(() => DEFAULT_ASSISTANT_NAME)
        loading.then(n => { if (alive) setName(n) })
        return () => { alive = false }
    }, [])
    return name
}
