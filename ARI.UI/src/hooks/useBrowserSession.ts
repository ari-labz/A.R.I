import { useCallback, useEffect, useRef, useState } from "react"
import { apiFetch } from "../auth"

export interface BrowserStatus { open: boolean; waiting?: boolean; prompt?: string | null; tabs?: string[]; active?: number }

export interface BrowserSession {
    /** The thread's browser endpoint, or null with no thread open. */
    base: string | null
    status: BrowserStatus
    /** Tucked away by the person; the button in the header and the cards in the chat bring it back. */
    hidden: boolean
    setHidden: (hidden: boolean) => void
    show: () => void
    toggle: () => void
}

// The state of one conversation's browser, shared by the header button, the docked pane and the "needs you" line. Is there a page?
// Checked every couple of seconds. It comes to the front by itself when Ari asks for the person, and a card in the chat brings it back too.
export function useBrowserSession(activeThread: string | null): BrowserSession {
    const [status, setStatus] = useState<BrowserStatus>({ open: false })
    const [hidden, setHidden] = useState(false)
    const wasWaiting = useRef(false)

    const base = activeThread ? `/api/v1/threads/${activeThread}/browser` : null

    useEffect(() => { setStatus({ open: false }); setHidden(false) }, [activeThread])

    useEffect(() => {
        if (!base) return
        let stop = false
        const tick = async () => {
            try {
                const r = await apiFetch(base)
                if (!stop && r.ok) setStatus(await r.json())
                else if (!stop) setStatus({ open: false })
            } catch { /* server busy */ }
        }
        tick()
        const t = setInterval(tick, 2000)
        return () => { stop = true; clearInterval(t) }
    }, [base])

    useEffect(() => {
        if (status.waiting && !wasWaiting.current) setHidden(false)
        wasWaiting.current = !!status.waiting
    }, [status.waiting])

    // A browser card in a message (it carries data-open-browser) brings the pane back.
    useEffect(() => {
        const onClick = (e: MouseEvent) => { if ((e.target as Element | null)?.closest?.("[data-open-browser]")) setHidden(false) }
        const onKey = (e: KeyboardEvent) => {
            if ((e.key === "Enter" || e.key === " ") && (e.target as Element | null)?.closest?.("[data-open-browser]")) { e.preventDefault(); setHidden(false) }
        }
        document.addEventListener("click", onClick)
        document.addEventListener("keydown", onKey)
        return () => { document.removeEventListener("click", onClick); document.removeEventListener("keydown", onKey) }
    }, [])

    const show = useCallback(() => setHidden(false), [])
    const toggle = useCallback(() => setHidden(h => !h), [])
    return { base, status, hidden, setHidden, show, toggle }
}
