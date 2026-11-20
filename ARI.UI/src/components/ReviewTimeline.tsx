import type { ThreadItem } from "../hooks/useThreads"

interface Step { who: string; what: string; s: number; kind?: string }

const MARK = /<!--ari-review:([A-Za-z0-9+/=]+)-->/

/** The review loop's run, if this reply went through one: who did what, in order, and how long each took. */
export function reviewStepsOf(parts: ThreadItem[]): Step[] | null {
    for (const p of parts) {
        const text = [p.content, ...(p.blocks ?? []).map(b => b.text ?? "")].join("\n")
        const m = text.match(MARK)
        if (!m) continue
        try { return JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(m[1]), c => c.charCodeAt(0)))).steps as Step[] } catch { return null }
    }
    return null
}

// Under the reply: a one-line summary ("checked twice") that unfolds into the timeline of the run.
export default function ReviewTimeline({ steps }: { steps: Step[] }) {
    const checks = steps.filter(s => s.kind === "fail" || s.kind === "pass" || s.kind === "settle")
    const fails = steps.filter(s => s.kind === "fail")
    const end = steps.find(s => s.kind === "end")
    const summary = checks.length <= 1 ? "checked once" : checks.length === 2 ? "checked twice" : `checked ${checks.length} times`
    return (
        <div className="msg-row assistant review-tl-row">
            <details className="review-tl">
                <summary>↻ {summary}{fails.length ? ` · ${fails.length} fix${fails.length > 1 ? "es" : ""}` : ""}{end ? ` · ${end.s}s` : ""}</summary>
                <div className="review-tl-grid">
                    {steps.map((s, i) => (
                        <div key={i} className="review-tl-step">
                            <b>{s.who}</b>
                            <span className={`review-tl-what ${s.kind ?? ""}`}>{s.what}</span>
                            <span className="review-tl-s">{s.s}s</span>
                        </div>
                    ))}
                </div>
            </details>
        </div>
    )
}
