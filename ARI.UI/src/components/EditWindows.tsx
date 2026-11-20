import { useEffect, useMemo, useRef, useState } from "react"
import type { ThreadItem } from "../hooks/useThreads"

/** One file Ari edited this turn: the changed lines with a little context either side, before and after. */
export interface EditWin {
    id:     string      // the file's path: editing the same file again updates its window
    version: string     // changes with each edit, so the window replays
    file:   string
    line:   number      // first changed line (1-based)
    before: string[]
    old:    string[]
    neu:    string[]
    after:  string[]
}

const EDIT_MARK = /<!--ari-edit:([A-Za-z0-9+/=]+)-->/g

function decode(b64: string): EditWin | null {
    try {
        const j = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(b64), c => c.charCodeAt(0))))
        return { id: j.p, version: b64.slice(-24), file: j.f, line: j.l, before: j.b ?? [], old: j.o ?? [], neu: j.n ?? [], after: j.a ?? [] }
    } catch { return null }
}

/** The files Ari has edited since the user's last message. A new message clears them. */
export function editWindowsFrom(items: ThreadItem[]): EditWin[] {
    const found = new Map<string, EditWin>()
    for (const item of items) {
        if (item.type === "userMessage") { found.clear(); continue }
        if (item.type !== "ariResponse") continue
        const text = [item.content, ...(item.blocks ?? []).map(b => b.text ?? "")].join("\n")
        for (const [, b64] of text.matchAll(EDIT_MARK)) {
            const w = decode(b64)
            if (!w) continue
            found.delete(w.id)
            found.set(w.id, w)
        }
    }
    return [...found.values()]
}

// For a one-line change, strike only the words that differ; otherwise strike the whole old lines.
function splitLine(o: string, n: string): { pre: string; oldMid: string; newMid: string; post: string } {
    let p = 0
    while (p < o.length && p < n.length && o[p] === n[p]) p++
    let s = 0
    while (s < o.length - p && s < n.length - p && o[o.length - 1 - s] === n[n.length - 1 - s]) s++
    return { pre: o.slice(0, p), oldMid: o.slice(p, o.length - s), newMid: n.slice(p, n.length - s), post: o.slice(o.length - s) }
}

type Phase = 0 | 1 | 2   // 0 the section as it was, 1 the old part struck through, 2 the new text (typed)

function usePlay(w: EditWin, live: boolean, total: number) {
    const [phase, setPhase] = useState<Phase>(live ? 0 : 2)
    const [typed, setTyped] = useState(live ? 0 : total)
    useEffect(() => {
        if (!live) { setPhase(2); setTyped(total); return }
        setPhase(0); setTyped(0)
        let t2: number | undefined, iv: number | undefined
        const t1 = window.setTimeout(() => {
            setPhase(1)
            t2 = window.setTimeout(() => {
                setPhase(2)
                iv = window.setInterval(() => setTyped(n => { if (n >= total) { window.clearInterval(iv); return n } return n + 1 }), 22)
            }, 650)
        }, w.old.length ? 900 : 300)
        return () => { clearTimeout(t1); clearTimeout(t2); clearInterval(iv) }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [w.version, live])
    return { phase, typed }
}

function Body({ w, live }: { w: EditWin; live: boolean }) {
    const oneLine = w.old.length === 1 && w.neu.length === 1
    const split   = oneLine ? splitLine(w.old[0], w.neu[0]) : null
    const newText = oneLine ? split!.newMid : w.neu.join("\n")
    const { phase, typed } = usePlay(w, live, newText.length)
    const shown = newText.slice(0, typed)
    const typing = phase === 2 && typed < newText.length
    const ln = (key: string, text: string, cls = "") => <div key={key} className={`ew-line ${cls}`}>{text || " "}</div>

    return (
        <div className="ew-code">
            {w.before.map((t, i) => ln("b" + i, t, "ew-ctx"))}
            {oneLine ? (
                <div className="ew-line">
                    {split!.pre}
                    {split!.oldMid && <span className={phase >= 1 ? "ew-del" : ""}>{split!.oldMid}</span>}
                    {phase >= 2 && shown && <span className="ew-add">{shown}{typing && <i className="ew-caret" />}</span>}
                    {split!.post}
                </div>
            ) : (
                <>
                    {w.old.map((t, i) => <div key={"o" + i} className={`ew-line ${phase >= 1 ? "ew-del" : ""}`}>{t || " "}</div>)}
                    {phase >= 2 && w.neu.length > 0 && <div className="ew-line ew-add ew-block">{shown}{typing && <i className="ew-caret" />}</div>}
                </>
            )}
            {w.after.map((t, i) => ln("a" + i, t, "ew-ctx"))}
        </div>
    )
}

interface Props { windows: EditWin[]; leaving: boolean }

// Beside the chat: one window per edited file, in the space the chat leaves. The newest is open and the earlier
// ones fold to a title strip in a staircase; click a strip to reopen it (the others fold).
export default function EditWindows({ windows, leaving }: Props) {
    const [open, setOpen] = useState<string | null>(null)
    const [entered, setEntered] = useState<Set<string>>(new Set())
    const seen = useRef<Set<string> | null>(null)
    const [live, setLive] = useState<Set<string>>(new Set())
    const newest = windows[windows.length - 1]?.id ?? null

    // Windows that exist when this first renders (a thread loaded from history) show their finished diff; ones
    // that arrive afterwards are played.
    useEffect(() => {
        if (seen.current === null) { seen.current = new Set(windows.map(w => w.id + w.version)); return }
        const fresh = windows.filter(w => !seen.current!.has(w.id + w.version))
        if (!fresh.length) return
        fresh.forEach(w => seen.current!.add(w.id + w.version))
        setLive(l => new Set([...l, ...fresh.map(w => w.id + w.version)]))
        setOpen(fresh[fresh.length - 1].id)
    }, [windows])
    useEffect(() => { if (open === null && newest) setOpen(newest) }, [open, newest])
    useEffect(() => {
        const ids = windows.filter(w => !entered.has(w.id)).map(w => w.id)
        if (!ids.length) return
        const t = requestAnimationFrame(() => requestAnimationFrame(() => setEntered(e => new Set([...e, ...ids]))))
        return () => cancelAnimationFrame(t)
    }, [windows, entered])

    const order = useMemo(() => windows.map(w => w.id), [windows])
    return (
        <div className={`edit-pane${leaving ? " leaving" : ""}`}>
            {windows.map((w, i) => {
                const folded = open !== w.id
                const added = w.neu.length, removed = w.old.length
                return (
                    <div key={w.id}
                         className={`ew${folded ? " folded" : ""}${entered.has(w.id) ? " in" : ""}`}
                         style={{ marginLeft: folded ? order.indexOf(w.id) * 14 : 0, transitionDelay: leaving ? `${(windows.length - 1 - i) * 60}ms` : undefined }}>
                        <button type="button" className="ew-bar" onClick={() => setOpen(folded ? w.id : null)} title={folded ? "Show the changes" : "Fold"}>
                            <span className="ew-name">{w.file}</span>
                            <span className="ew-meta">
                                {added > 0 && <span className="ew-cnt-add">+{added}</span>}
                                {removed > 0 && <span className="ew-cnt-del"> −{removed}</span>}
                                <span className="ew-line-no"> · line {w.line}</span>
                            </span>
                        </button>
                        <div className="ew-fold"><div className="ew-fold-in"><Body w={w} live={live.has(w.id + w.version)} /></div></div>
                    </div>
                )
            })}
        </div>
    )
}
