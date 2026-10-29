import { apiFetch } from "./auth"

// Visuals Ari draws with show_visual: the message holds an empty <div class="ari-visual" data-visual-src>, and this fills it with a
// sandboxed iframe. The frame has no access to the app (sandbox without allow-same-origin, so it has a null origin), may only load scripts
// and styles from two CDNs, and cannot make network requests, so a page she writes can show things and respond to clicks but cannot read
// your chats or send anything anywhere.

const CSP = "default-src 'none'; script-src 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net; " +
            "style-src 'unsafe-inline' https://fonts.googleapis.com https://cdnjs.cloudflare.com https://cdn.jsdelivr.net; " +
            "img-src data: blob:; font-src data: https://fonts.gstatic.com https://cdnjs.cloudflare.com https://cdn.jsdelivr.net; connect-src 'none'; form-action 'none'"

// The frame follows the app's own theme (not the machine's): light text when the chat is dark, dark text when it is light.
function baseStyle(): string {
    const m = getComputedStyle(document.body).color.match(/\d+/g)?.map(Number) ?? [16, 22, 24]
    const light = (m[0] * 299 + m[1] * 587 + m[2] * 114) / 1000 > 140
    return `<style>html,body{margin:0;background:transparent;font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;color:${light ? "#CADDE2" : "#101618"}}svg{max-width:100%;height:auto}</style>`
}

const cache = new Map<string, string>()
let nextId = 1
const frames = new Map<string, HTMLIFrameElement>()

function wrap(html: string, id: string): string {
    // Report the page's height to the parent so the frame grows to fit it.
    const report = `<script>(function(){function s(){parent.postMessage({ariVisual:"${id}",height:Math.ceil(document.documentElement.scrollHeight)},"*")}` +
                   `addEventListener("load",s);new ResizeObserver(s).observe(document.documentElement);setTimeout(s,300)})()</script>`
    const head = `<meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${CSP}">${baseStyle()}`
    return /<html[\s>]/i.test(html)
        ? html.replace(/<head[^>]*>/i, m => m + head).replace(/<\/body>/i, report + "</body>") + (/<\/body>/i.test(html) ? "" : report)
        : `<!doctype html><html><head>${head}</head><body>${html}${report}</body></html>`
}

async function mount(el: HTMLElement) {
    const src = el.dataset.visualSrc
    if (!src || el.dataset.mounted) return
    el.dataset.mounted = "1"
    try {
        let html = cache.get(src)
        if (html === undefined) {
            const r = await apiFetch(src)
            if (!r.ok) throw new Error(String(r.status))
            html = await r.text(); cache.set(src, html)
        }
        const id = String(nextId++)
        const frame = document.createElement("iframe")
        frame.setAttribute("sandbox", "allow-scripts")
        frame.setAttribute("referrerpolicy", "no-referrer")
        frame.title = el.dataset.visualName ?? "Visual"
        frame.style.cssText = "width:100%;height:260px;border:0;display:block;background:transparent;color-scheme:normal"
        frame.srcdoc = wrap(html, id)
        frames.set(id, frame)
        el.replaceChildren(frame)
    } catch {
        el.textContent = "Couldn't load this visual."
    }
}

let started = false
export function startVisuals() {
    if (started) return
    started = true
    window.addEventListener("message", e => {
        const d = e.data
        if (!d || typeof d !== "object" || typeof d.ariVisual !== "string" || typeof d.height !== "number") return
        const f = frames.get(d.ariVisual)
        if (f && e.source === f.contentWindow) f.style.height = Math.min(Math.max(d.height, 60), 1400) + "px"
    })
    const scan = (root: ParentNode) => root.querySelectorAll?.<HTMLElement>(".ari-visual:not([data-mounted])").forEach(mount)
    new MutationObserver(muts => {
        for (const m of muts) m.addedNodes.forEach(n => { if (n instanceof HTMLElement) { if (n.matches?.(".ari-visual")) mount(n); else scan(n) } })
    }).observe(document.body, { childList: true, subtree: true })
    scan(document)
}
