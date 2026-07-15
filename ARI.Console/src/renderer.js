const $ = id => document.getElementById(id)
const consoleEl = $("console")
const MAX_LINES = 2000

// ── Log stream ───────────────────────────────────────────────────────────────

function appendLine(line) {
    const div = document.createElement("div")
    div.className = "line " + levelClass(line)
    div.textContent = line
    // Auto-scroll only if already pinned to the bottom.
    const pinned = consoleEl.scrollHeight - consoleEl.scrollTop - consoleEl.clientHeight < 24
    consoleEl.appendChild(div)
    while (consoleEl.childElementCount > MAX_LINES) consoleEl.removeChild(consoleEl.firstChild)
    if (pinned) consoleEl.scrollTop = consoleEl.scrollHeight
}

function levelClass(line) {
    if (line.includes("[FATAL]")) return "fatal"
    if (line.includes("[ERROR]")) return "error"
    if (line.includes("[WARN]")) return "warn"
    return ""
}

window.ari.onLog(appendLine)

// ── Status ───────────────────────────────────────────────────────────────────

function renderState({ status, version, endpoint }) {
    const dot  = $("dot")
    const text = $("status-text")
    dot.className = status
    text.textContent = status === "running" ? "Running" : status === "starting" ? "Starting…" : "Stopped"
    $("version").textContent = !version ? "" : version === "dev" ? "dev" : `v${version}`
    if (endpoint) $("endpoint").textContent = endpoint

    // Stop button doubles as Start when the server is down.
    const stop = $("btn-stop")
    if (status === "stopped") { stop.textContent = "▶ Start"; stop.dataset.action = "start" }
    else                      { stop.textContent = "■ Stop";  stop.dataset.action = "stop" }
}

window.ari.onStatus?.(renderState)

// ── Controls ─────────────────────────────────────────────────────────────────

$("btn-stop").addEventListener("click", () => {
    if ($("btn-stop").dataset.action === "start") window.ari.start()
    else window.ari.stop()
})
$("btn-restart").addEventListener("click", () => window.ari.restart())
$("btn-open").addEventListener("click",    () => window.ari.open())
$("btn-copy").addEventListener("click",    () => window.ari.copy())
$("btn-logs").addEventListener("click",    () => window.ari.logs())
$("btn-config").addEventListener("click",  () => window.ari.config())

window.ari.state?.().then(renderState)
