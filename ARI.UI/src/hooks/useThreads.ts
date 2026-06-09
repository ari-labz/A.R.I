import { useState, useCallback, useRef } from "react"

export interface ThreadEntry {
    key:          string
    isInternal:   boolean
    agentName:    string | null
    isCodeMode:   boolean
    state:        string
    lastMessageAt: string
    projectName?: string | null
    projectId?:   string | null
}

export interface Project {
    id:                string
    name:              string
    description:       string
    instructions:      string
    localPath:         string | null
    forceCodePipeline: boolean
}

export function useThreads() {
    const [threads, setThreads] = useState<ThreadEntry[]>([])

    const load = useCallback(async () => {
        try {
            const res = await fetch("/api/threads")
            if (!res.ok) return
            const data: ThreadEntry[] = await res.json()
            setThreads(data)
        } catch { /* ignore */ }
    }, [])

    return { threads, load }
}

export interface ThreadItem {
    type:            string
    content:         string
    username?:       string
    timestamp:       string
    thinkingSeconds?: number
    recallNotes?:    string
    contextSummary?: string
    input?:          string
    response?:       string
    changes?:        MemoryChange[]
    attachments?:    Attachment[]
    isImage?:        boolean
    mimeType?:       string
    name?:           string
}

export interface MemoryChange {
    title:   string
    url:     string | null
    op:      string
    summary: string
}

export interface Attachment {
    name:     string
    isImage:  boolean
    mimeType: string | null
    content:  string | null
}

export async function createThread(projectId?: string | null): Promise<string> {
    const res = await fetch("/api/threads", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ projectId: projectId ?? null }),
    })
    const { key } = await res.json()
    return key
}

export async function loadHistory(key: string, raw = false): Promise<ThreadItem[]> {
    const url = raw ? `/api/threads/${key}/history?raw=true` : `/api/threads/${key}/history`
    const res = await fetch(url)
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    return res.json()
}

export function openWatchStream(
    key: string,
    onEvent: (data: WatchEvent) => void,
    onError: () => void,
): EventSource {
    const es = new EventSource(`/api/threads/${key}/watch`)
    es.onmessage = e => {
        try { onEvent(JSON.parse(e.data)) } catch { /* ignore */ }
    }
    es.onerror = onError
    return es
}

export interface WatchEvent {
    deleted?:    boolean
    isProcessing?: boolean
    isRemembering?: boolean
    isCodeMode?: boolean
}

export async function cancelProcessing(key: string) {
    await fetch(`/api/threads/${key}/processing`, { method: "DELETE" })
}

export function useTypingHeartbeat(getThreadKey: () => string | null) {
    const timer = useRef<ReturnType<typeof setInterval> | null>(null)

    function send() {
        const key = getThreadKey()
        if (!key) return
        fetch(`/api/threads/${key}/typing`, { method: "POST" }).catch(() => {})
    }

    function start() {
        if (timer.current) return
        send()
        timer.current = setInterval(send, 3000)
    }

    function stop() {
        if (timer.current) { clearInterval(timer.current); timer.current = null }
    }

    return { start, stop }
}
