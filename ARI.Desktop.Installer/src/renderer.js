const screens = {
    token:    document.getElementById("screen-token"),
    progress: document.getElementById("screen-progress"),
    error:    document.getElementById("screen-error"),
}

const statusText    = document.getElementById("status-text")
const progressBar   = document.getElementById("progress-bar")
const progressLabel = document.getElementById("progress-label")
const errorText     = document.getElementById("error-text")
const tokenInput    = document.getElementById("token-input")

function show(name) {
    for (const [k, el] of Object.entries(screens))
        el.classList.toggle("hidden", k !== name)
}

function setStatus(msg) { statusText.textContent = msg }

function setProgress(pct, received, total) {
    progressBar.style.width = `${pct}%`
    if (total > 0) {
        const mb = n => (n / 1_048_576).toFixed(1)
        progressLabel.textContent = `${mb(received)} MB / ${mb(total)} MB`
    }
}

function showError(msg, allowRetry = true) {
    errorText.textContent = msg
    document.getElementById("btn-retry").style.display = allowRetry ? "" : "none"
    show("error")
}

window.launcher.onProgress(({ pct, received, total }) => setProgress(pct, received, total))
window.launcher.onStatus(s => setStatus(s))

document.getElementById("btn-save-token").addEventListener("click", async () => {
    const t = tokenInput.value.trim()
    if (!t) return
    await window.launcher.saveToken(t)
    show("progress")
    await run(t)
})

tokenInput.addEventListener("keydown", e => {
    if (e.key === "Enter") document.getElementById("btn-save-token").click()
})

document.getElementById("token-help").addEventListener("click", e => {
    e.preventDefault()
    // Open in default browser via shell - not possible here directly,
    // but the text gives enough context
    alert("Go to github.com/settings/tokens and create a token with the 'repo' scope.")
})

document.getElementById("btn-retry").addEventListener("click", () => {
    show("progress")
    start()
})

async function start() {
    const token = await window.launcher.getToken()
    if (!token) { show("token"); return }
    await run(token)
}

async function run(token) {
    show("progress")
    setStatus("Checking for updates...")
    setProgress(0, 0, 0)
    progressLabel.textContent = ""

    let release
    try {
        release = await window.launcher.fetchRelease(token)
    } catch (e) {
        if (e.message === "TOKEN_INVALID") {
            tokenInput.value = ""
            document.getElementById("token-message").textContent =
                "Token is invalid or expired. Please enter a new one."
            show("token")
            return
        }
        showError(e.message)
        return
    }

    const installed = await window.launcher.versionInstalled(release.tagName)
    if (installed) {
        setStatus(`Launching ${release.tagName}...`)
        try {
            await window.launcher.launchAri(null)
        } catch (e) {
            showError(e.message)
        }
        return
    }

    setStatus(`Downloading ${release.tagName}...`)
    let versionDir
    try {
        versionDir = await window.launcher.downloadAndInstall(token, release)
    } catch (e) {
        showError(e.message)
        return
    }

    setStatus(`Launching ${release.tagName}...`)
    try {
        await window.launcher.launchAri(versionDir)
    } catch (e) {
        showError(e.message)
    }
}

start()
