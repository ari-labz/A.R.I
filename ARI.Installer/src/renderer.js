const screens = {
    token:    document.getElementById("screen-token"),
    main:     document.getElementById("screen-main"),
    progress: document.getElementById("screen-progress"),
    done:     document.getElementById("screen-done"),
    error:    document.getElementById("screen-error"),
}

const $ = id => document.getElementById(id)
const statusText    = $("status-text")
const progressBar   = $("progress-bar")
const progressLabel = $("progress-label")

let token       = null
let releases    = []        // sorted newest-first
let installed   = null      // { version, protocol } | null
let selected    = null      // release object chosen from the list

function show(name) {
    for (const [k, el] of Object.entries(screens))
        el.classList.toggle("hidden", k !== name)
}

// Renders "·protocol v4·" in de-emphasised italic, or nothing if unknown.
function protoTag(protocol) {
    if (!Number.isInteger(protocol)) return `<span class="proto unknown">·protocol —·</span>`
    return `<span class="proto">·protocol v${protocol}·</span>`
}

function setStatus(msg) { statusText.textContent = msg }

function setProgress(pct, received, total) {
    progressBar.style.width = `${pct}%`
    if (total > 0) {
        const mb = n => (n / 1_048_576).toFixed(1)
        progressLabel.textContent = `${mb(received)} MB / ${mb(total)} MB`
    }
}

function showError(msg) {
    $("error-text").textContent = msg
    show("error")
}

window.installer.onProgress(({ pct, received, total }) => setProgress(pct, received, total))
window.installer.onStatus(s => setStatus(s))

// ── Main screen rendering ────────────────────────────────────────────────────

function renderInstalled() {
    if (installed)
        $("installed-line").innerHTML = `Server <b>${installed.version}</b> ${protoTag(installed.protocol)}`
    else
        $("installed-line").innerHTML = `<span class="muted">None installed</span>`
}

function renderVersionList() {
    const list = $("version-list")
    list.innerHTML = ""
    for (const r of releases) {
        const row = document.createElement("div")
        row.className = "version-row"
        if (selected && selected.tagName === r.tagName) row.classList.add("active")

        const badges = []
        if (r === releases[0]) badges.push(`<span class="badge">latest</span>`)
        if (installed && installed.version === r.version) badges.push(`<span class="badge installed">installed</span>`)
        if (r.prerelease) badges.push(`<span class="badge pre">beta</span>`)

        row.innerHTML =
            `<span class="ver">${r.version}</span>` +
            protoTag(r.protocol) +
            `<span class="badges">${badges.join("")}</span>`
        row.addEventListener("click", () => selectVersion(r))
        list.appendChild(row)
    }
}

function selectVersion(r) {
    selected = r
    renderVersionList()
    $("selected-line").classList.remove("hidden")
    $("selected-line").innerHTML = `Selected <b>${r.version}</b> ${protoTag(r.protocol)}`
    const btn = $("btn-install")
    btn.classList.remove("hidden")
    btn.textContent = `Install ${r.version}`
}

// ── Actions ──────────────────────────────────────────────────────────────────

$("btn-select-version").addEventListener("click", () => {
    $("version-list").classList.toggle("hidden")
})

$("btn-update-latest").addEventListener("click", () => {
    if (!releases.length) return
    selectVersion(releases[0])
    install()
})

$("btn-install").addEventListener("click", install)

async function install() {
    if (!selected) return
    show("progress")
    setStatus(`Downloading ${selected.version}…`)
    setProgress(0, 0, 0)
    progressLabel.textContent = ""

    const options = {
        addShortcut: $("toggle-shortcut").checked,
        startServer: $("toggle-start").checked,
    }
    try {
        installed = await window.installer.downloadAndInstall(token, selected, options)
    } catch (e) {
        showError(e.message)
        return
    }
    $("done-text").innerHTML = `Server <b>${installed.version}</b> ${protoTag(installed.protocol)} installed.`
    show("done")
}

$("btn-launch").addEventListener("click", async () => {
    try { await window.installer.launchServer(installed?.version || null) }
    catch (e) { showError(e.message); return }
    window.close()
})

$("btn-close").addEventListener("click", () => window.close())

// ── Token screen ─────────────────────────────────────────────────────────────

$("btn-save-token").addEventListener("click", async () => {
    const t = $("token-input").value.trim()
    if (!t) return
    await window.installer.saveToken(t)
    token = t
    await loadMain()
})

$("token-input").addEventListener("keydown", e => {
    if (e.key === "Enter") $("btn-save-token").click()
})

$("token-help").addEventListener("click", e => {
    e.preventDefault()
    alert("Go to github.com/settings/tokens and create a token with the 'repo' scope.")
})

$("btn-retry").addEventListener("click", start)

// ── Boot ─────────────────────────────────────────────────────────────────────

async function loadMain() {
    show("progress")
    setStatus("Checking for versions…")
    try {
        releases  = await window.installer.fetchReleases(token)
        installed = await window.installer.installedInfo()
    } catch (e) {
        if (e.message === "TOKEN_INVALID") {
            $("token-input").value = ""
            $("token-message").textContent = "Token is invalid or expired. Please enter a new one."
            show("token")
            return
        }
        showError(e.message)
        return
    }
    renderInstalled()
    renderVersionList()
    show("main")
}

async function start() {
    // Label the OS-specific shortcut toggle.
    const platform = await window.installer.getPlatform()
    const toggle   = $("toggle-shortcut").closest(".toggle")
    if (platform === "win32")      $("toggle-shortcut-label").textContent = "Add to Start Menu"
    else if (platform === "darwin") $("toggle-shortcut-label").textContent = "Add to Applications"
    else                            toggle.classList.add("hidden")   // no shortcut on Linux

    // A token is only needed while the repo is private (REPO_PRIVATE in main.js).
    const needsToken = await window.installer.needsToken()
    token = await window.installer.getToken()
    if (needsToken && !token) { show("token"); return }
    await loadMain()
}

start()
