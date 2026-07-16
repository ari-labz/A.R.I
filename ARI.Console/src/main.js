const { app, BrowserWindow, ipcMain, shell, clipboard } = require("electron")
const { spawn } = require("child_process")
const readline = require("readline")
const fs   = require("fs")
const path = require("path")
const os   = require("os")

// Safety net: never let a stray error tear the console window down — log it and keep running.
process.on("uncaughtException",  err => { try { emit(`[ERROR] ${err?.stack || err}`) } catch {} })
process.on("unhandledRejection", err => { try { emit(`[ERROR] ${err?.stack || err}`) } catch {} })

// ── Paths (mirror the installer's layout) ──────────────────────────────────────

function getBaseDir() {
    if (process.platform === "win32")
        return path.join(process.env.LOCALAPPDATA || os.homedir(), "ARI")
    if (process.platform === "darwin")
        return path.join(os.homedir(), "Library", "Application Support", "ARI")
    return path.join(os.homedir(), ".local", "share", "ARI")
}

const baseDir     = getBaseDir()
const serverDir   = path.join(baseDir, "server")
const currentFile = path.join(serverDir, "current.txt")

// AppData (config, logs) lives at ~/ARI — the server's default APP_DATA_ROOT.
const appDataDir  = process.platform === "win32"
    ? path.join(process.env.APPDATA || os.homedir(), "ARI")
    : path.join(os.homedir(), "ARI")
const logsDir     = path.join(appDataDir, "Server", "Logs")
const configFile  = path.join(appDataDir, "Server", "AriConfig.json")

function activeVersion() {
    if (fs.existsSync(currentFile)) {
        const v = fs.readFileSync(currentFile, "utf8").trim()
        if (v && fs.existsSync(path.join(serverDir, v))) return v
    }
    if (!fs.existsSync(serverDir)) return null
    const dirs = fs.readdirSync(serverDir)
        .filter(n => /^\d+\.\d+\.\d+/.test(n) && fs.statSync(path.join(serverDir, n)).isDirectory())
        .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }))
    return dirs[0] || null
}

function versionDir(v)  { return path.join(serverDir, v) }
function exePath(dir)   { return path.join(dir, process.platform === "win32" ? "ARI.Core.exe" : "ARI.Core") }

const isDev = process.env.NODE_ENV === "development"

// In dev the server is the dotnet build at APP_INSTALL_ROOT (default /Applications/A.R.I or
// %ProgramFiles%\A.R.I). ARI_DEV_SERVER overrides it.
function devServerRoot() {
    if (process.env.ARI_DEV_SERVER) return process.env.ARI_DEV_SERVER
    return process.platform === "win32"
        ? path.join(process.env.ProgramFiles || "C:\\Program Files", "A.R.I")
        : "/Applications/A.R.I"
}

function readVersion(root) {
    try { return JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8")).version } catch { return null }
}

// Which server build to run: the dev build in dev, else the active installed version.
function resolveServer() {
    if (isDev) {
        const root = devServerRoot()
        return { root, exe: exePath(root), version: readVersion(root) || "dev" }
    }
    const version = activeVersion()
    if (!version) return null
    const root = versionDir(version)
    return { root, exe: exePath(root), version }
}

function endpoint() {
    let port = 5074
    try { port = JSON.parse(fs.readFileSync(configFile, "utf8"))?.Modules?.API?.Port || 5074 } catch {}
    return `http://localhost:${port}`
}

// ── Server process management ───────────────────────────────────────────────────

let win
let child         = null
let status        = "stopped"   // stopped | starting | running
let serverVersion = null
let quitting      = false

function setStatus(s) {
    status = s
    win?.webContents.send("status", { status, version: serverVersion, endpoint: endpoint() })
}

function startServer() {
    if (child) return
    const srv = resolveServer()
    if (!srv) { emit("[ERROR] No installed server version found. Install one with the A·R·I installer."); setStatus("stopped"); return }
    if (!fs.existsSync(srv.exe)) { emit(`[ERROR] Server executable not found: ${srv.exe}`); setStatus("stopped"); return }

    serverVersion = srv.version
    setStatus("starting")
    emit(`Starting A·R·I server ${srv.version}…`)
    child = spawn(srv.exe, [], { cwd: srv.root, env: { ...process.env, APP_INSTALL_ROOT: srv.root } })

    for (const stream of [child.stdout, child.stderr]) {
        // A killed server (stop/restart) breaks these pipes; without an error handler the stream's
        // 'error' event goes unhandled and crashes the whole console. Swallow it — the exit handler
        // below reports the stop.
        stream.on("error", () => {})
        const rl = readline.createInterface({ input: stream })
        rl.on("error", () => {})
        rl.on("line", line => {
            emit(line)
            if (line.includes("ARI is ready")) setStatus("running")
        })
    }
    child.on("exit", () => { child = null; emit("Server stopped."); setStatus("stopped") })
    child.on("error", err => { emit(`[ERROR] Failed to start server: ${err.message}`); child = null; setStatus("stopped") })
}

// SIGTERM lets ARI.Core run its own shutdown (which stops llama-server / whisper).
function stopServer() {
    return new Promise(resolve => {
        if (!child) return resolve()
        setStatus("stopped")
        const proc = child
        const done = setTimeout(() => { try { proc.kill("SIGKILL") } catch {} resolve() }, 8000)
        proc.once("exit", () => { clearTimeout(done); resolve() })
        try { proc.kill("SIGTERM") } catch { resolve() }
    })
}

function emit(line) { win?.webContents.send("log", line) }

// ── Window ──────────────────────────────────────────────────────────────────────

app.whenReady().then(() => {
    win = new BrowserWindow({
        width: 640, height: 460, minWidth: 480, minHeight: 360,
        titleBarStyle: "hidden", trafficLightPosition: { x: 16, y: 16 },
        backgroundColor: "#161f24",
        icon: path.join(__dirname, "..", "assets", "icon.png"),
        webPreferences: { preload: path.join(__dirname, "preload.js"), contextIsolation: true },
    })
    win.loadFile(path.join(__dirname, "index.html"))
    win.webContents.once("did-finish-load", () => { setStatus("stopped"); startServer() })   // auto-start

    win.on("close", e => {
        if (quitting) return
        e.preventDefault()
        quitting = true
        emit("Stopping server…")
        stopServer().finally(() => win.destroy())
    })
})

app.on("window-all-closed", () => app.quit())

// ── IPC ─────────────────────────────────────────────────────────────────────────

ipcMain.handle("state",   () => ({ status, version: serverVersion, endpoint: endpoint() }))
ipcMain.handle("start",   () => startServer())
ipcMain.handle("stop",    () => stopServer())
ipcMain.handle("restart", async () => { await stopServer(); startServer() })
ipcMain.handle("open",    () => shell.openExternal(endpoint()))
ipcMain.handle("copy",    () => clipboard.writeText(endpoint()))
ipcMain.handle("logs",    () => shell.openPath(logsDir))
ipcMain.handle("config",  () => shell.openPath(configFile))
