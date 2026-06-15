import { useEffect, useRef, useState, useCallback } from "react"
import type { ThreadItem, Attachment } from "../hooks/useThreads"
import { setBubbleMd } from "../hooks/useMarkdown"

// ── Speak response ────────────────────────────────────────────────────────────
let globalSpeakAbort: AbortController | null = null
// One shared AudioContext for the entire speak session — avoids cold-start per sentence
let sharedCtx: AudioContext | null = null

function getAudioCtx(): AudioContext {
    if (!sharedCtx || sharedCtx.state === "closed")
        sharedCtx = new AudioContext()
    return sharedCtx
}

function getVolume(): number {
    return Math.max(0, parseFloat(localStorage.getItem("ari-voice-volume") ?? "100")) / 100
}

// Fetches audio for a sentence and immediately decodes it into an AudioBuffer.
// Decoding happens in parallel with synthesis of other sentences.
async function synthesise(sentence: string, signal: AbortSignal): Promise<AudioBuffer | null> {
    try {
        const resp = await fetch("/api/cp/voice/speak", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ text: sentence }),
            signal,
        })
        if (!resp.ok) return null
        const ab = await resp.arrayBuffer()
        if (signal.aborted) return null
        return await getAudioCtx().decodeAudioData(ab)
    } catch {
        return null
    }
}

// Schedules an AudioBuffer to play at `startAt` (AudioContext time).
// Returns a Promise that resolves with the exact scheduled end time,
// enabling gapless back-to-back scheduling of the next sentence.
function scheduleBuffer(buf: AudioBuffer, startAt: number, signal: AbortSignal): Promise<number> {
    const ctx  = getAudioCtx()
    const gain = ctx.createGain()
    gain.gain.value = getVolume()
    const src  = ctx.createBufferSource()
    src.buffer = buf
    src.connect(gain)
    gain.connect(ctx.destination)

    // If we're behind (synthesis took longer than previous playback), play immediately
    const playAt = Math.max(startAt, ctx.currentTime)
    const endAt  = playAt + buf.duration
    src.start(playAt)

    return new Promise((resolve, reject) => {
        const onAbort = () => { src.stop(); reject(new DOMException("aborted")) }
        signal.addEventListener("abort", onAbort, { once: true })
        // onended fires when playback finishes — resolve with the precise end time
        src.onended = () => { signal.removeEventListener("abort", onAbort); resolve(endAt) }
    })
}

async function speakResponse(content: string, setSpeaking: (v: boolean) => void) {
    if (globalSpeakAbort) {
        globalSpeakAbort.abort()
        globalSpeakAbort = null
    }

    const abort = new AbortController()
    globalSpeakAbort = abort
    setSpeaking(true)

    try {
        const splitRes = await fetch("/api/cp/voice/split-sentences", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ text: content }),
            signal: abort.signal,
        })
        if (!splitRes.ok) throw new Error("split failed")
        const { sentences } = await splitRes.json() as { sentences: string[] }
        if (sentences.length === 0) return

        // Start decoding sentence[0] immediately.
        // As soon as sentence[i] is ready and scheduled, we start synthesising sentence[i+1]
        // so it decodes in parallel while sentence[i] is playing.
        let nextBuffer = synthesise(sentences[0], abort.signal)
        // scheduleAt tracks the precise AudioContext time at which the next sentence should start,
        // enabling gapless chaining when audio is ready in time.
        let scheduleAt = getAudioCtx().currentTime

        for (let i = 0; i < sentences.length; i++) {
            if (abort.signal.aborted) break

            const buf = await nextBuffer
            if (abort.signal.aborted) break

            // Kick off synthesis of the next sentence immediately — it decodes in parallel
            // with playback of the current one
            if (i + 1 < sentences.length)
                nextBuffer = synthesise(sentences[i + 1], abort.signal)

            if (!buf) continue

            // Schedule this sentence to start exactly when the previous one ends.
            // scheduleBuffer returns the end time so we can chain the next sentence.
            scheduleAt = await scheduleBuffer(buf, scheduleAt, abort.signal)
        }
    } catch (err: unknown) {
        if (err instanceof Error && err.name !== "AbortError") console.warn("[speak]", err)
    } finally {
        if (globalSpeakAbort === abort) {
            globalSpeakAbort = null
            setSpeaking(false)
        }
    }
}

function SpeakButton({ content }: { content: string }) {
    const [speaking, setSpeaking] = useState(false)

    const handleClick = useCallback(() => {
        if (speaking) {
            // Stop
            globalSpeakAbort?.abort()
            globalSpeakAbort = null
            setSpeaking(false)
        } else {
            speakResponse(content, setSpeaking)
        }
    }, [speaking, content])

    return (
        <button
            className={`btn-speak${speaking ? " speaking" : ""}`}
            title={speaking ? "Stop speaking" : "Speak response"}
            onClick={handleClick}
        >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/>
                <path d="M19.07 4.93a10 10 0 0 1 0 14.14"/>
                <path d="M15.54 8.46a5 5 0 0 1 0 7.07"/>
            </svg>
        </button>
    )
}

interface Props {
    items:        ThreadItem[]
    isRemembering: boolean
    activeThread: string | null
    isInternal:   boolean
    agentName:    string | null
}

function fileExtLabel(name: string) {
    const dot = name.lastIndexOf(".")
    return dot >= 0 ? name.slice(dot + 1).toUpperCase().slice(0, 4) : "FILE"
}

function formatTime(ts: string) {
    if (!ts || ts.startsWith("0001")) return ""
    return new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
}

function MdBubble({ content, className, msgIndex = 0 }: { content: string; className: string; msgIndex?: number }) {
    const ref = useRef<HTMLDivElement>(null)
    useEffect(() => {
        if (ref.current) setBubbleMd(ref.current, content, msgIndex)
    }, [content, msgIndex])
    return <div ref={ref} className={className} />
}

function UserMessage({ item, activeThread }: { item: ThreadItem; activeThread: string | null }) {
    const t = formatTime(item.timestamp)
    const attachments = (item.attachments ?? []) as Attachment[]
    return (
        <>
            {attachments.map((a, i) => (
                <div key={i} className={`msg-row user ${a.isImage ? "attach-image" : "attach-file"}`} style={{ maxWidth: "var(--col-max)", margin: "0 auto" }}>
                    <div>
                        {a.isImage ? (
                            <img className="msg-image" src={
                                a.content
                                    ? `data:${a.mimeType ?? "image/jpeg"};base64,${a.content}`
                                    : `/api/threads/${activeThread}/msg-attachment?name=${encodeURIComponent(a.name)}`
                            } alt={a.name} />
                        ) : (
                            <div className="file-card">
                                <div className="file-card-icon">{fileExtLabel(a.name)}</div>
                                <div className="file-card-name" title={a.name}>{a.name}</div>
                            </div>
                        )}
                        <div className="msg-time">{t}</div>
                    </div>
                </div>
            ))}
            {item.content && (
                <div className="msg-row user">
                    <div>
                        <MdBubble content={item.content} className="bubble" />
                        <div className="msg-time">{t}</div>
                    </div>
                </div>
            )}
        </>
    )
}

function AriResponse({ item, isInternal, agentName, msgIndex }: { item: ThreadItem; isInternal: boolean; agentName: string | null; msgIndex: number }) {
    const streaming = item.isStreaming ?? false
    const t = formatTime(item.timestamp)
    const senderLabel = isInternal ? (agentName ?? "Agent") : "A·R·I"

    let thoughtEl: React.ReactNode = null
    if (!streaming && item.thinkingSeconds != null) {
        const secs = typeof item.thinkingSeconds === "number"
            ? item.thinkingSeconds.toFixed(1) : item.thinkingSeconds
        const hasDetails = !!(item.recallNotes || item.contextSummary)
        if (hasDetails) {
            thoughtEl = (
                <details className="thought-block">
                    <summary>A·R·I thought for {secs}s</summary>
                    <div className="thought-content">
                        {item.recallNotes && <RecallNotes raw={item.recallNotes} />}
                        {item.contextSummary && <><h4>Context summary</h4>{item.contextSummary}</>}
                    </div>
                </details>
            )
        } else {
            thoughtEl = <div style={{ fontSize: "12px", color: "#8e8ea0", marginTop: "6px", userSelect: "none" }}>A·R·I thought for {secs}s</div>
        }
    }

    return (
        <div className="msg-row assistant">
            <div className="sender">{senderLabel}</div>
            {item.content && <MdBubble content={item.content} className="bubble" msgIndex={msgIndex} />}
            {streaming && (
                <div className="typing-indicator">
                    <span>A·R·I is thinking</span>
                    <div className="typing-dots"><b /><b /><b /></div>
                </div>
            )}
            {thoughtEl}
            {!streaming && (
                <div className="msg-footer">
                    <div className="msg-time">{t}</div>
                    {item.content && <SpeakButton content={item.content} />}
                </div>
            )}
        </div>
    )
}

function RecallNotes({ raw }: { raw: string }) {
    const blocks = raw.split(/\n(?=\[)/).map(block => {
        const match = block.match(/^\[([^|\]]+)(?:\|([^\]]+))?\]\n?([\s\S]*)/)
        return match ? { name: match[1], url: match[2] || null, content: match[3].trim() } : null
    }).filter(Boolean) as { name: string; url: string | null; content: string }[]

    return (
        <div className="recall-notes-section">
            <span className="recall-label">Notes Read</span>
            {blocks.map((n, i) => (
                <details key={i} className="recall-note">
                    <summary>
                        {n.url
                            ? <a href={n.url} target="_blank" rel="noopener" className="recall-note-link">{n.name}</a>
                            : n.name}
                    </summary>
                    <div className="recall-note-content">{n.content}</div>
                </details>
            ))}
        </div>
    )
}

function CommandInput({ item }: { item: ThreadItem }) {
    const t = formatTime(item.timestamp)
    return (
        <div className="msg-row command-input-row">
            <div className="command-input">{item.input}</div>
            <div className="msg-time">{t}</div>
        </div>
    )
}

function CommandResponse({ item }: { item: ThreadItem }) {
    const t = formatTime(item.timestamp)
    return (
        <div className="msg-row command-response-row">
            <MdBubble content={item.response ?? ""} className="command-response-block" />
            <div className="msg-time">{t}</div>
        </div>
    )
}

function MemoryEvent({ item }: { item: ThreadItem }) {
    const t = formatTime(item.timestamp)
    const changes = item.changes ?? []
    return (
        <div className="msg-row memory-event">
            <details className="thought-block memory-block">
                <summary>A·R·I will remember this</summary>
                <div className="thought-content memory-content">
                    {changes.map((c, i) => (
                        <div key={i} className="memory-change">
                            {c.url
                                ? <a href={c.url} target="_blank" rel="noopener" className="recall-note-link">{c.title}</a>
                                : c.title}
                            {" — "}
                            {c.op === "created"
                                ? <span className="memory-op memory-op-created">created</span>
                                : <><span className="memory-op memory-op-updated">updated</span> <span className="memory-summary">{c.summary}</span></>}
                        </div>
                    ))}
                </div>
            </details>
            {t && <div className="msg-time">{t}</div>}
        </div>
    )
}

// Guards done-card badges from re-animating across React re-renders.
const animatedDoneBadges = new Set<string>()
// Tracks the last CUMULATIVE value each active badge was animated TO.
// e.g. if edit 1 removed 44 lines and edit 2 is streaming, this holds 44+streaming_count.
const activeBadgeValues = new Map<string, number>()   // badgeId → last displayed cumulative value
// The cumulative total BEFORE the current streaming edit started (= runningTotals at that moment).
// Needed to convert each streaming delta (0→N) into cumulative (prevTotal→prevTotal+N).
const activeBadgeBases  = new Map<string, number>()   // badgeId → base total at edit start
// Running totals per file+direction so sequential done cards chain correctly.
// "edit_file:File.cs:add" → cumulative count after all COMPLETED edits
const runningTotals = new Map<string, number>()

// Strip message index + occurrence to get the stable per-file key.
// "edit_file:File.cs:5:0:add" → "edit_file:File.cs:add"
function fileKeyFromBadgeId(badgeId: string): string {
    return badgeId.replace(/:\d+:\d+:(add|del)$/, ":$1")
}

function animateBadge(badge: HTMLElement, d: HTMLElement, from: number, to: number, dir: string) {
    const lineH = 16
    d.style.lineHeight  = `${lineH}px`
    badge.style.height   = `${lineH}px`
    badge.style.overflow = "hidden"
    d.style.minWidth     = `${Math.max(String(from).length, String(to).length)}ch`
    // Mark the element with the value it will display so that animateDiffBadges can detect
    // when a freshly-created DOM node (which always starts at "0") needs to be initialised.
    d.dataset.current = String(to)

    if (from === to) {
        d.textContent = ""
        const s = document.createElement("span"); s.textContent = String(to); d.appendChild(s)
        return
    }

    const steps = Math.min(Math.abs(to - from), 20)
    const raw: number[] = []
    for (let i = 0; i <= steps; i++) raw.push(Math.round(from + (to - from) * (i / steps)))
    const col = raw.filter((n, i) => i === 0 || n !== raw[i - 1])

    d.textContent = ""
    if (dir === "down") {
        const rev = [...col].reverse()
        rev.forEach(n => { const s = document.createElement("span"); s.textContent = String(n); d.appendChild(s) })
        d.style.transition = "none"
        d.style.transform  = `translateY(-${(rev.length - 1) * lineH}px)`
        requestAnimationFrame(() => requestAnimationFrame(() => {
            d.style.transition = "transform 0.6s cubic-bezier(0.22,1,0.36,1)"
            d.style.transform  = "translateY(0)"
        }))
    } else {
        col.forEach(n => { const s = document.createElement("span"); s.textContent = String(n); d.appendChild(s) })
        d.style.transition = "none"
        d.style.transform  = "translateY(0)"
        requestAnimationFrame(() => requestAnimationFrame(() => {
            d.style.transition = "transform 0.6s cubic-bezier(0.22,1,0.36,1)"
            d.style.transform  = `translateY(-${(col.length - 1) * lineH}px)`
        }))
    }
}

function animateDiffBadges(root: HTMLElement) {
    root.querySelectorAll<HTMLElement>(".diff-badge[data-target]").forEach(badge => {
        const target  = parseInt(badge.dataset.target ?? "0", 10)
        const dir     = badge.dataset.dir ?? "up"
        const isDone  = !!badge.closest(".tool-card--done")
        const badgeId = badge.dataset.badgeId ?? ""

        const d = badge.querySelector<HTMLElement>(".badge-digits")
        if (!d) return

        if (isDone) {
            // Done card: animate once from wherever the active card left off → actual cumulative.
            const doneKey = `done:${badgeId}`
            const fileKey = fileKeyFromBadgeId(badgeId)
            const base = activeBadgeBases.get(badgeId) ?? (runningTotals.get(fileKey) ?? 0)
            const to   = base + target   // base (before this edit) + actual count = correct cumulative

            if (animatedDoneBadges.has(doneKey)) {
                // Already animated once — but innerHTML replacement creates a fresh element
                // that shows "0".  Re-set it directly without re-animating.
                // Use runningTotals (the value stored when the badge was first animated)
                // rather than re-computing base+target, because activeBadgeBases has already
                // been deleted and runningTotals already includes this edit's contribution.
                const storedTo = runningTotals.get(fileKey) ?? to
                if (d.dataset.current !== String(storedTo)) animateBadge(badge, d, storedTo, storedTo, dir)
                return
            }
            animatedDoneBadges.add(doneKey)
            runningTotals.set(fileKey, to)

            const from = activeBadgeValues.get(badgeId) ?? base
            activeBadgeValues.delete(badgeId)
            activeBadgeBases.delete(badgeId)
            animateBadge(badge, d, from, to, dir)
        } else {
            // Active card: each streaming update grows target (delta for THIS edit, not cumulative).
            // Convert to cumulative using the base total captured when this edit started.
            const fileKey = fileKeyFromBadgeId(badgeId)

            // Capture base total once per edit (first time this badgeId is seen).
            if (!activeBadgeBases.has(badgeId))
                activeBadgeBases.set(badgeId, runningTotals.get(fileKey) ?? 0)

            const base       = activeBadgeBases.get(badgeId)!
            const cumulative = base + target   // what we should display now
            // Guard: skip if the DOM element already shows the correct value.
            // We use d.dataset.current (set by animateBadge) rather than activeBadgeValues alone,
            // because innerHTML replacement creates fresh elements that start at "0" regardless
            // of what activeBadgeValues says — we must not skip those.
            if (d.dataset.current === String(cumulative)) return

            const from = activeBadgeValues.get(badgeId) ?? base
            activeBadgeValues.set(badgeId, cumulative)
            animateBadge(badge, d, from, cumulative, dir)
        }
    })
}

export default function Messages({ items, isRemembering, activeThread, isInternal, agentName }: Props) {
    const bottomRef  = useRef<HTMLDivElement>(null)
    const messagesEl = useRef<HTMLDivElement>(null)

    useEffect(() => {
        bottomRef.current?.scrollIntoView({ behavior: "smooth" })
    }, [items, isRemembering])

    useEffect(() => {
        const el = messagesEl.current
        if (!el) return
        // Animate any badges already present
        animateDiffBadges(el)
        // Watch for new ones as streaming adds tool cards
        const observer = new MutationObserver(() => animateDiffBadges(el))
        observer.observe(el, { childList: true, subtree: true })
        return () => observer.disconnect()
    }, [])

    return (
        <div id="messages" ref={messagesEl}>
            {items.map((item, i) => {
                switch (item.type) {
                    case "userMessage":
                        return <UserMessage key={i} item={item} activeThread={activeThread} />
                    case "ariResponse":
                        return <AriResponse key={i} item={item} isInternal={isInternal} agentName={agentName} msgIndex={i} />
                    case "commandInput":
                        return <CommandInput key={i} item={item} />
                    case "commandResponse":
                        return <CommandResponse key={i} item={item} />
                    case "engramEvent":
                        return <MemoryEvent key={i} item={item} />
                    default:
                        return null
                }
            })}

            {isRemembering && (
                <div className="msg-row assistant" id="typing-indicator">
                    <div className="sender">A·R·I</div>
                    <div className="typing-indicator">
                        <span>Remembering</span>
                        <div className="typing-dots"><b /><b /><b /></div>
                    </div>
                </div>
            )}

            <div ref={bottomRef} />
        </div>
    )
}
