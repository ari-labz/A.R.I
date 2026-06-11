import { useEffect, useRef } from "react"
import type { ThreadItem, Attachment } from "../hooks/useThreads"
import { setBubbleMd } from "../hooks/useMarkdown"

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

function MdBubble({ content, className }: { content: string; className: string }) {
    const ref = useRef<HTMLDivElement>(null)
    useEffect(() => {
        if (ref.current) setBubbleMd(ref.current, content)
    }, [content])
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

function AriResponse({ item, isInternal, agentName }: { item: ThreadItem; isInternal: boolean; agentName: string | null }) {
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
            {item.content && <MdBubble content={item.content} className="bubble" />}
            {streaming && (
                <div className="typing-indicator">
                    <span>A·R·I is thinking</span>
                    <div className="typing-dots"><b /><b /><b /></div>
                </div>
            )}
            {thoughtEl}
            {!streaming && <div className="msg-time">{t}</div>}
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

function CommandExchange({ item }: { item: ThreadItem }) {
    const t = formatTime(item.timestamp)
    return (
        <div className="msg-row command-exchange">
            <div className="command-input">{item.input}</div>
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

function buildDigitColumn(digitsEl: HTMLElement, nums: number[]) {
    digitsEl.textContent = ""
    nums.forEach(n => {
        const s = document.createElement("span")
        s.style.display = "block"
        s.textContent = String(n)
        digitsEl.appendChild(s)
    })
}

function animateDiffBadges(root: HTMLElement) {
    root.querySelectorAll<HTMLElement>(".diff-badge[data-target]").forEach(badge => {
        const target  = parseInt(badge.dataset.target ?? "0", 10)
        const dir     = badge.dataset.dir ?? "up"
        const isLive  = !!badge.closest(".tool-card--active")
        if (!target || badge.dataset.animated) return
        badge.dataset.animated = "1"

        const digitsEl = badge.querySelector<HTMLElement>(".badge-digits")
        if (!digitsEl) return
        const d = digitsEl

        const lineH = 16
        digitsEl.style.lineHeight = `${lineH}px`
        badge.style.height        = `${lineH}px`
        badge.style.overflow      = "hidden"

        if (isLive) {
            // Continuous rolling animation while editing is in progress.
            // Counts from 0 up to target repeatedly with a smooth scroll, direction based on dir.
            const steps = Math.min(target, 20)
            const nums: number[] = []
            for (let i = 0; i <= steps; i++) nums.push(Math.round((i / steps) * target))

            // For "down" direction, reverse the visual order so it scrolls the other way
            const col = dir === "down" ? [...nums].reverse() : nums
            buildDigitColumn(d, [...col, ...col]) // doubled for seamless loop

            const totalH = col.length * lineH
            d.style.transition = "none"
            d.style.transform  = "translateY(0)"

            let running = true
            badge.dataset.liveStop = "0"

            function roll() {
                if (!running) return
                d.style.transition = `transform ${0.6 + Math.random() * 0.3}s cubic-bezier(0.22,1,0.36,1)`
                d.style.transform  = `translateY(-${totalH}px)`
                const t = setTimeout(() => {
                    if (!running) return
                    d.style.transition = "none"
                    d.style.transform  = "translateY(0)"
                    requestAnimationFrame(() => requestAnimationFrame(roll))
                }, 900 + Math.random() * 200)
                badge.dataset.liveTimer = String(t)
            }
            requestAnimationFrame(() => requestAnimationFrame(roll))

            // Stop rolling when the card transitions to done (active class removed)
            const obs = new MutationObserver(() => {
                if (!badge.closest(".tool-card--active")) {
                    running = false
                    clearTimeout(Number(badge.dataset.liveTimer))
                    obs.disconnect()
                    // Snap to final value — let the done-card re-render handle the end animation
                }
            })
            const card = badge.closest(".tool-card")
            if (card) obs.observe(card, { attributes: true, attributeFilter: ["class"] })

        } else {
            // Done card: single sweep from 0 → target
            const steps = Math.min(target, 20)
            const nums: number[] = []
            for (let i = 0; i <= steps; i++) nums.push(Math.round((i / steps) * target))

            const col = dir === "down" ? [...nums].reverse() : nums
            buildDigitColumn(d, col)

            d.style.transform = "translateY(0)"
            requestAnimationFrame(() => requestAnimationFrame(() => {
                d.style.transform = `translateY(-${(col.length - 1) * lineH}px)`
            }))
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
                        return <AriResponse key={i} item={item} isInternal={isInternal} agentName={agentName} />
                    case "commandExchange":
                        return <CommandExchange key={i} item={item} />
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
