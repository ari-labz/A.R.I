import { marked } from "marked"
import hljs from "highlight.js"
import "highlight.js/styles/vs2015.css"

marked.setOptions({ breaks: true, gfm: true })

const COPY_ICON = `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>`
const CHECK_ICON = `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>`

const TOOL_START_RE = /<!--ari-tool-start:([^:]+):([^>]*?)-->/g
const TOOL_END_RE   = /<!--ari-tool-end:([^:]+):([^>]*?)-->/g
const TOOL_ERROR_RE = /<!--ari-tool-error:([^:]+):([^>]*?)-->/g

const TOOL_VERBS: Record<string, { active: string; done: string }> = {
    read_file:      { active: "Reading",          done: "Read" },
    list_directory: { active: "Listing",           done: "Listed" },
    search_files:   { active: "Searching",         done: "Searched" },
    edit_file:      { active: "Editing",           done: "Edited" },
    write_file:     { active: "Writing",           done: "Written" },
}

function preprocessToolCards(content: string): string {
    const done = new Set<string>()
    for (const m of content.matchAll(TOOL_END_RE)) done.add(`${m[1]}:${m[2]}`)
    let out = content.replace(TOOL_END_RE, "")
    out = out.replace(TOOL_ERROR_RE, (_, _name, label) => {
        const msg = label.replace(/&#45;&#45;/g, "--").replace(/&gt;/g, ">")
        return `\n\n<div class="tool-card tool-card--error"><span>Error: ${msg}</span></div>\n\n`
    })
    out = out.replace(TOOL_START_RE, (_, name, label) => {
        const file    = label.replace(/&#45;&#45;/g, "--")
        const verbs   = TOOL_VERBS[name] ?? { active: name, done: name }
        const key     = `${name}:${label}`
        if (done.has(key)) {
            return `\n\n<div class="tool-card tool-card--done"><span>${verbs.done} ${file}</span></div>\n\n`
        }
        return `\n\n<div class="tool-card tool-card--active"><span>${verbs.active} ${file}</span><div class="typing-dots"><b></b><b></b><b></b></div></div>\n\n`
    })
    return out
}

export function renderMd(content: string): string {
    const html = marked.parse(preprocessToolCards(content ?? ""), { async: false }) as string
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

export function setBubbleMd(el: HTMLElement, content: string) {
    el.innerHTML = renderMd(content)
    attachCopyButtons(el)
}
