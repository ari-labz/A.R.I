import { useState, useCallback, useRef } from "react"
import { env } from "../env"

export interface ThreadEntry {
    key:          string
    isInternal:   boolean
    agentName:    string | null
    isCodeMode:   boolean
    pipeline:     string  // "dialogue" | "code" | "speech"
    state:        string
    lastMessageAt: string
    projectName?: string | null
    projectId?:   string | null
    title?:       string | null
}

export interface Project {
    id:                string
    name:              string
    description:       string
    instructions:      string
    forceCodePipeline: boolean
}

export function useThreads() {
    const [threads, setThreads] = useState<ThreadEntry[]>([])

    const load = useCallback(async () => {
        try {
            const res = await fetch("/threads")
            if (!res.ok) return
            const data: ThreadEntry[] = await res.json()
            setThreads(data)
        } catch { /* ignore */ }
    }, [])

    return { threads, load }
}

// One typed block of a Response, serialized polymorphically by the server (`type` discriminator).
// `state` is the numeric State enum (0=streaming, 1=complete, 2=error, 3=cancelled). Card subtypes carry
// their own fields (fileName / path / pattern / command / task / project / added / removed / patch / …).
export interface ContentBlock {
    type:       string
    state:      number
    isVisible:  boolean
    text?:      string
    fileName?:  string
    path?:      string
    pattern?:   string
    command?:   string
    task?:      string
    project?:   string
    added?:     number
    removed?:   number
    patch?:     string
    // subthread anchor: a labelled, inline child thread whose blocks render nested here
    label?:     string
    blocks?:    ContentBlock[]
}

export interface ThreadItem {
    type:            string
    content:         string
    blocks?:         ContentBlock[]
    username?:       string
    timestamp:       string
    thinkingSeconds?: number
    prefillSeconds?:  number
    typingSeconds?:   number
    totalSeconds?:    number
    recallNotes?:    string
    contextSummary?: string
    input?:          string
    response?:       string
    changes?:        MemoryChange[]
    attachments?:    Attachment[]
    isImage?:        boolean
    mimeType?:       string
    name?:           string
    isStreaming?:    boolean
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

export async function createThread(projectId?: string | null, pipeline?: string | null): Promise<string> {
    const res = await fetch("/threads", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ projectId: projectId ?? null, desktop: env.isDesktop, pipeline: pipeline ?? null }),
    })
    const { key } = await res.json()
    return key
}

// Close a thread: the server runs Engram (saving it to memory) then deletes it, broadcasting threadDeleted.
export async function closeThread(key: string): Promise<boolean> {
    const res = await fetch(`/threads/${key}`, { method: "DELETE" })
    return res.ok
}

export async function loadHistory(key: string, raw = false): Promise<ThreadItem[]> {
    const url = raw ? `/threads/${key}/history?raw=true` : `/threads/${key}/history`
    const res = await fetch(url)
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    return res.json()
}

export interface ThreadDetail {
    key:           string
    state:         string  // "idle" | "streaming" | "dormant" | "cleanupneeded" | "deleted"
    pipeline:      string  // "dialogue" | "code" | "speech"
    isInternal:    boolean
    lastMessageAt: string
    history:       ThreadItem[]
}

export async function fetchThread(key: string): Promise<ThreadDetail | null> {
    try {
        const res = await fetch(`/threads/${key}`)
        if (!res.ok) return null
        return res.json()
    } catch { return null }
}

/** Polls GET /threads/{key} every 150ms until state is no longer "streaming", calling onUpdate each tick.
 *  Returns a stop function. Safe to call stop() multiple times. */
export function pollThreadWhileStreaming(
    key: string,
    onUpdate: (detail: ThreadDetail) => void,
): () => void {
    let active = true
    let handle: ReturnType<typeof setTimeout>

    async function tick() {
        if (!active) return
        const detail = await fetchThread(key)
        if (!active) return
        if (detail) {
            onUpdate(detail)
            if (detail.state === "streaming") {
                handle = setTimeout(tick, 150)
                return
            }
        }
        active = false
    }

    tick()
    return () => { active = false; clearTimeout(handle) }
}

export function openWatchStream(
    key: string,
    onEvent: (data: WatchEvent) => void,
    onError: () => void,
): EventSource {
    const es = new EventSource(`/threads/${key}/watch`)
    es.onmessage = e => {
        try { onEvent(JSON.parse(e.data)) } catch { /* ignore */ }
    }
    es.onerror = onError
    return es
}

export interface WatchEvent {
    deleted?:       boolean
    isProcessing?:  boolean
    isRemembering?: boolean
    isCodeMode?:    boolean
}

export interface AppEvent {
    type:       "newThread" | "streaming" | "streamingFinished" | "threadDeleted" | "threadUpdated"
    threadKey:  string
    text?:      string | null
}

export function openEventStream(
    onEvent: (data: AppEvent) => void,
    onError: () => void,
): EventSource {
    const es = new EventSource("/events")
    es.onmessage = e => {
        try { onEvent(JSON.parse(e.data)) } catch { /* ignore */ }
    }
    es.onerror = onError
    return es
}

export async function cancelProcessing(key: string) {
    await fetch(`/threads/${key}/processing`, { method: "DELETE" })
}

export function useTypingHeartbeat(getThreadKey: () => string | null) {
    const timer = useRef<ReturnType<typeof setInterval> | null>(null)

    function send() {
        const key = getThreadKey()
        if (!key) return
        fetch(`/threads/${key}/typing`, { method: "POST" }).catch(() => {})
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
