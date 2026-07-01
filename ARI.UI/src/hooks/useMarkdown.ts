import { marked } from "marked"
import hljs from "highlight.js"
import "highlight.js/styles/vs2015.css"

marked.setOptions({ breaks: true, gfm: true })

const COPY_ICON = `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>`
const CHECK_ICON = `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>`

const TOOL_START_RE = /<!--ari-tool-start:([^:]+):([^>]*?)-->/g
const TOOL_END_RE   = /<!--ari-tool-end:([^:]+):([^>]*?)-->/g
const TOOL_ERROR_RE = /<!--ari-tool-error:([^:]+):([^:]*):([^>]*?)-->/g

function escHtml(s: string): string {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
}

const TOOL_VERBS: Record<string, { active: string; done: string }> = {
    read_file:      { active: "Reading",          done: "Read" },
    list_directory: { active: "Listing",           done: "Listed" },
    search_files:   { active: "Searching",         done: "Searched" },
    edit_file:      { active: "Editing",           done: "Edited" },
    write_file:     { active: "Writing",           done: "Written" },
    run_command:    { active: "Running",           done: "Ran" },
    find_files:     { active: "Finding",           done: "Found" },
    delete_file:    { active: "Deleting",          done: "Deleted" },
    move_file:      { active: "Moving",            done: "Moved" },
}

function parseDiffLabel(rawLabel: string): { fileLabel: string; added: number; removed: number; patch: string } | null {
    const parts = rawLabel.split("|")
    if (parts.length < 2) return null
    const fileLabel = parts[0]
    let added = 0, removed = 0, encoded = ""
    if (parts[1]?.startsWith("+") && parts[2]?.startsWith("-")) {
        added   = parseInt(parts[1].slice(1)) || 0
        removed = parseInt(parts[2].slice(1)) || 0
        encoded = parts[3] ?? ""
    } else if (parts[1]?.startsWith("+")) {
        added   = parseInt(parts[1].slice(1)) || 0
        encoded = parts[2] ?? ""
    } else {
        return null
    }
    let patch = ""
    if (encoded) { try { patch = atob(encoded) } catch { /* ignore */ } }
    return { fileLabel, added, removed, patch }
}

// Strips leftover <tool_call>/<function=...>/<parameter=...> XML that the fallback
// parser leaves behind when it can only partially consume a text-format tool call.
const TOOL_CALL_XML_RE = /<tool_call>[\s\S]*?<\/tool_call>|<tool_call>[\s\S]*$|<\/function[^>]*>|<function=[^>]*>[\s\S]*?<\/function[^>]*>|<function=[^>]*>[\s\S]*$|<parameter=[^>]*>[\s\S]*?<\/parameter>|<\/parameter>/g

function preprocessToolCards(content: string, msgIndex = 0): string {
    content = content.replace(TOOL_CALL_XML_RE, "")

    // Isolate raw tool-use div cards as their own block (blank line before/after). Otherwise a card sitting
    // directly against prose (e.g. "...`File.cs`.<div class=\"tool-use\">…") makes `marked` treat the whole
    // prose line as a raw HTML block and skip inline markdown — so `code` shows literal backticks (issue #69).
    content = content.replace(/[ \t]*\n?[ \t]*(<div class="tool-use">[\s\S]*?<\/div>)[ \t]*\n?[ \t]*/g, "\n\n$1\n\n")

    // Step 1: collect diff data from enriched end markers as an ordered queue.
    // Using a queue (not a map) so multiple edits to the same file each get their own data,
    // matched in order with start markers.
    const diffQueue: Array<{ key: string; added: number; removed: number; patch: string }> = []
    let normalized = content.replace(TOOL_END_RE, (full, name, rawLabel) => {
        const parsed = parseDiffLabel(rawLabel)
        if (!parsed) return full
        diffQueue.push({ key: `${name}:${parsed.fileLabel}`, added: parsed.added, removed: parsed.removed, patch: parsed.patch })
        return `<!--ari-tool-end:${name}:${parsed.fileLabel}-->`
    })

    // Step 2: count done/error signals per key (multiset) so multiple calls to the same file each match.
    const doneCount  = new Map<string, number>()
    const errorCount = new Map<string, number>()
    const BATCH_END = "<!--ari-batch-end-->"
    const ERROR_RE_G = new RegExp(TOOL_ERROR_RE.source, "g")
    for (const m of normalized.matchAll(ERROR_RE_G)) {
        const k = `${m[1]}:${m[2]}`
        errorCount.set(k, (errorCount.get(k) ?? 0) + 1)
    }
    // A tool card finalizes (→ "done") as soon as its batch completes — i.e. its end marker is followed
    // by a batch-end marker. We deliberately do NOT also require content after the batch-end: that made a
    // card linger on "Editing…" until the model resumed talking (very visible with long edits) and left the
    // last card stuck forever when a turn ended right after an edit.
    const END_RE_G = new RegExp(TOOL_END_RE.source, "g")
    for (const m of normalized.matchAll(END_RE_G)) {
        const after = m.index! + m[0].length
        if (normalized.indexOf(BATCH_END, after) < 0) continue
        const k = `${m[1]}:${m[2]}`
        doneCount.set(k, (doneCount.get(k) ?? 0) + 1)
    }

    // Occurrence counter so multiple edits to the same file get unique badge IDs.
    const occurrence = new Map<string, number>()

    let out = normalized.split(BATCH_END).join("")
    out = out.replace(TOOL_END_RE, "")
    out = out.replace(TOOL_ERROR_RE, (_, name, rawFile, rawMsg) => {
        const verbs = TOOL_VERBS[name] ?? { active: name, done: name }
        const file  = rawFile.replace(/&#45;&#45;/g, "--").replace(/&gt;/g, ">")
        const msg   = rawMsg.replace(/&#45;&#45;/g, "--").replace(/&gt;/g, ">")
        return `\n\n<div class="tool-card tool-card--error"><span>${verbs.active} ${escHtml(file)}</span><span class="tool-card-error-msg">${escHtml(msg)}</span></div>\n\n`
    })
    out = out.replace(TOOL_START_RE, (_, name, label) => {
        const file      = label.replace(/&#45;&#45;/g, "--")
        const cleanFile = file.replace(/\|\+\d+(?:\|-\d+)?$/, "")
        const verbs = TOOL_VERBS[name] ?? { active: name, done: name }
        const key   = `${name}:${cleanFile}`
        const occ   = occurrence.get(key) ?? 0
        occurrence.set(key, occ + 1)
        const badgeKey = `${key}:${msgIndex}:${occ}`
        // If there's a matching error, suppress the start card — the error card renders at the error position.
        const errRemaining = errorCount.get(key) ?? 0
        if (errRemaining > 0) {
            errorCount.set(key, errRemaining - 1)
            return ""
        }
        const remaining = doneCount.get(key) ?? 0
        if (remaining > 0) {
            doneCount.set(key, remaining - 1)
            // Dequeue the first diff entry matching this key (preserves order for same-file edits).
            const idx = diffQueue.findIndex(item => item.key === key)
            const diff = idx >= 0 ? diffQueue.splice(idx, 1)[0] : null
            if (diff) {
                const addBadge = diff.added   > 0 ? `<span class="diff-badge diff-badge--add" data-target="${diff.added}" data-dir="up" data-badge-id="${badgeKey}:add">+<span class="badge-digits">0</span></span>`   : ""
                const delBadge = diff.removed > 0 ? `<span class="diff-badge diff-badge--del" data-target="${diff.removed}" data-dir="down" data-badge-id="${badgeKey}:del">-<span class="badge-digits">0</span></span>` : ""
                const badges = `<span class="diff-badges">${addBadge}${delBadge}</span>`
                if (diff.patch) {
                    const lines = diff.patch.split("\n").map(l => {
                        if (l.startsWith("+")) return `<div class="diff-line diff-line--add">${escHtml(l)}</div>`
                        if (l.startsWith("-")) return `<div class="diff-line diff-line--del">${escHtml(l)}</div>`
                        return `<div class="diff-line">${escHtml(l)}</div>`
                    }).join("")
                    return `\n\n<details class="tool-card tool-card--done tool-card--diff"><summary><span>${verbs.done} ${escHtml(cleanFile)}</span>${badges}</summary><div class="tool-card-diff">${lines}</div></details>\n\n`
                }
                // Has diff stats but no patch — too large to encode. Still expandable.
                return `\n\n<details class="tool-card tool-card--done tool-card--diff"><summary><span>${verbs.done} ${escHtml(cleanFile)}</span>${badges}</summary><div class="tool-card-diff tool-card-diff--too-large">Diff is too large to display</div></details>\n\n`
            }
            return `\n\n<div class="tool-card tool-card--done"><span>${verbs.done} ${escHtml(cleanFile)}</span></div>\n\n`
        }
        // Parse optional counts from start marker label (e.g. "File.cs|+12|-5")
        const countMatch = label.match(/\|\+(\d+)(?:\|-(\d+))?$/)
        const addCount = countMatch ? parseInt(countMatch[1]) : 0
        const delCount = countMatch ? parseInt(countMatch[2] ?? "0") : 0
        const addBadgeLive = addCount > 0 ? `<span class="diff-badge diff-badge--add" data-target="${addCount}" data-dir="up" data-badge-id="${badgeKey}:add">+<span class="badge-digits">0</span></span>` : ""
        const delBadgeLive = delCount > 0 ? `<span class="diff-badge diff-badge--del" data-target="${delCount}" data-dir="down" data-badge-id="${badgeKey}:del">-<span class="badge-digits">0</span></span>` : ""
        const liveBadges = (addBadgeLive || delBadgeLive) ? `<span class="diff-badges">${addBadgeLive}${delBadgeLive}</span>` : `<div class="typing-dots"><b></b><b></b><b></b></div>`
        return `\n\n<div class="tool-card tool-card--active"><span>${verbs.active} ${escHtml(cleanFile)}</span>${liveBadges}</div>\n\n`
    })
    return out
}

// Present/past verb pairs for the non-diff cards (extends TOOL_VERBS with the orchestration cards).
const CARD_VERBS: Record<string, { active: string; done: string }> = {
    reading:    { active: "Reading",    done: "Read" },
    listing:    { active: "Listing",    done: "Listed" },
    searching:  { active: "Searching",  done: "Searched" },
    finding:    { active: "Finding",    done: "Found" },
    running:    { active: "Running",    done: "Ran" },
    deleting:   { active: "Deleting",   done: "Deleted" },
    moving:     { active: "Moving",     done: "Moved" },
    delegating: { active: "Delegating", done: "Delegated" },
    building:   { active: "Building",   done: "Built" },
    editing:    { active: "Editing",    done: "Edited" },
    writing:    { active: "Writing",    done: "Written" },
}

// State enum: 0=streaming, 1=complete, 2=error, 3=cancelled.
type BlockLike = {
    type: string; state?: number
    text?: string; fileName?: string; path?: string; pattern?: string; command?: string
    task?: string; project?: string; added?: number; removed?: number; patch?: string
    label?: string; blocks?: BlockLike[]
}

function diffBadges(added = 0, removed = 0): string {
    const a = added   > 0 ? `<span class="diff-badge diff-badge--add" data-target="${added}" data-dir="up" data-static="1">+<span class="badge-digits">${added}</span></span>`   : ""
    const d = removed > 0 ? `<span class="diff-badge diff-badge--del" data-target="${removed}" data-dir="down" data-static="1">-<span class="badge-digits">${removed}</span></span>` : ""
    return (a || d) ? `<span class="diff-badges">${a}${d}</span>` : ""
}

// Renders ONE typed ContentBlock to HTML. Text blocks become markdown; cards become their tool-card markup
// straight from typed fields (no marker pairing) so each block is a self-contained DOM segment.
export function renderBlockHtml(block: BlockLike): string {
    const done = (block.state ?? 0) === 1
    const err  = (block.state ?? 0) === 2

    if (block.type === "text")     return marked.parse(block.text ?? "", { async: false }) as string
    if (block.type === "thinking") return ""   // reasoning is shown via the thought-block, not inline

    // Subthread anchor: a child thread rendered inline. Default-expanded and, when open, styled to look like
    // the blocks simply belong to the parent (no bubble chrome) — collapsing hides them behind the label.
    if (block.type === "subthread") {
        const inner = (block.blocks ?? []).map(renderBlockHtml).filter(h => h.trim().length > 0)
            .map(h => `<div class="block-seg">${h}</div>`).join("")
        return `<details class="subthread" open><summary class="subthread-head">${escHtml(block.label ?? "")}</summary><div class="subthread-body">${inner}</div></details>`
    }

    const verbs = CARD_VERBS[block.type] ?? { active: block.type, done: block.type }
    const label = block.fileName ?? block.path ?? block.pattern ?? block.command ?? block.task ?? block.project ?? ""

    if (block.type === "editing" || block.type === "writing") {
        const badges = diffBadges(block.added, block.removed)
        const cls = err ? "tool-card--error" : done ? "tool-card--done tool-card--diff" : "tool-card--active"
        if (done && block.patch) {
            const lines = block.patch.split("\n").map(l =>
                l.startsWith("+") ? `<div class="diff-line diff-line--add">${escHtml(l)}</div>`
                : l.startsWith("-") ? `<div class="diff-line diff-line--del">${escHtml(l)}</div>`
                : `<div class="diff-line">${escHtml(l)}</div>`).join("")
            return `<details class="tool-card ${cls}"><summary><span>${verbs.done} ${escHtml(label)}</span>${badges}</summary><div class="tool-card-diff">${lines}</div></details>`
        }
        const verb = err ? verbs.active : done ? verbs.done : verbs.active
        return `<div class="tool-card ${cls}"><span>${verb} ${escHtml(label)}</span>${badges}</div>`
    }

    const cls = err ? "tool-card--error" : done ? "tool-card--done" : "tool-card--active"
    const verb = err ? verbs.active : done ? verbs.done : verbs.active
    return `<div class="tool-card ${cls}"><span>${verb} ${escHtml(label)}</span></div>`
}

export function renderMd(content: string, msgIndex = 0): string {
    const html = marked.parse(preprocessToolCards(content ?? "", msgIndex), { async: false }) as string
    const tmp = document.createElement("div")
    tmp.innerHTML = html
    tmp.querySelectorAll("pre").forEach(pre => {
        const wrapper = document.createElement("div")
        wrapper.className = "code-block"
        pre.parentNode!.insertBefore(wrapper, pre)
        wrapper.appendChild(pre)
    })
    return tmp.innerHTML
}

export function attachCopyButtons(el: HTMLElement) {
    el.querySelectorAll<HTMLElement>(".code-block").forEach(wrapper => {
        if (wrapper.querySelector(".code-header")) return
        const pre = wrapper.querySelector("pre")
        if (!pre) return

        const codeEl = pre.querySelector("code")
        const langClass = codeEl?.className.match(/language-(\S+)/)
        const lang = langClass ? langClass[1] : ""

        if (codeEl && lang) hljs.highlightElement(codeEl)

        const header = document.createElement("div")
        header.className = "code-header"

        const langSpan = document.createElement("span")
        langSpan.className = "code-lang"
        langSpan.textContent = lang || "code"

        const btn = document.createElement("button")
        btn.className = "code-copy-btn"
        btn.title = "Copy code"
        btn.innerHTML = COPY_ICON
        btn.addEventListener("click", () => {
            const text = codeEl?.innerText ?? pre.innerText
            navigator.clipboard.writeText(text).then(() => {
                btn.innerHTML = CHECK_ICON
                btn.classList.add("copied")
                setTimeout(() => { btn.innerHTML = COPY_ICON; btn.classList.remove("copied") }, 2000)
            })
        })

        header.appendChild(langSpan)
        header.appendChild(btn)
        wrapper.insertBefore(header, pre)
    })
}

export function setBubbleMd(el: HTMLElement, content: string, msgIndex = 0) {
    el.innerHTML = renderMd(content, msgIndex)
    attachCopyButtons(el)
}

// Renders a Response's typed block list — each block is its own DOM segment, so the plan text, each
// Coder's card + summary, and the final summary are visually distinct blocks (not one flattened blob).
export function setBubbleBlocks(el: HTMLElement, blocks: BlockLike[]) {
    const parts = blocks
        .filter(b => (b as { isVisible?: boolean }).isVisible !== false)
        .map(b => renderBlockHtml(b))
        .filter(h => h.trim().length > 0)
    el.innerHTML = ""
    for (const html of parts) {
        const seg = document.createElement("div")
        seg.className = "block-seg"
        seg.innerHTML = html
        el.appendChild(seg)
    }
    attachCopyButtons(el)
}
