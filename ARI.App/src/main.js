// ── Bootstrap: install deps before requiring anything from node_modules ───────
const fs   = require("fs")
const path = require("path")

const appDir      = path.join(__dirname, "..")
const needsInstall = !fs.existsSync(path.join(appDir, "node_modules", "electron-store"))

if (needsInstall) {
    const { execSync } = require("child_process")
    const bunPath = process.platform === "win32"
        ? path.join(process.env.USERPROFILE ?? "", ".bun", "bin", "bun.exe")
        : path.join(process.env.HOME ?? "", ".bun", "bin", "bun")
    const installer = fs.existsSync(bunPath) ? `"${bunPath}"` : "npm"
    console.log("[ARI.App] Installing dependencies…")
    execSync(`${installer} install`, { cwd: appDir, stdio: "inherit" })
    console.log("[ARI.App] Dependencies ready.")
}

// ── Main ──────────────────────────────────────────────────────────────────────
const { app, BrowserWindow, ipcMain, dialog, session } = require("electron")
const Store = require("electron-store")
const { readFile, writeFile, getFileTree, listDirectory, searchFiles, editFile } = require("./fs")
const { init: initLogger, makeLogger, getLogPath } = require("./logger")

const store = new Store()
const isDev = process.env.NODE_ENV === "development"
const AUTH_COOKIE = ".AspNetCore.Cookies"

let win
let splash
let appReadyResolve
const appReady = new Promise(resolve => { appReadyResolve = resolve })

// Initialise logger as early as possible so we capture everything
initLogger(app.getPath("userData"))
const log = makeLogger("ARI.App")

log.info(`ARI client starting  (electron ${process.versions.electron}, node ${process.versions.node})`)
log.info(`Platform: ${process.platform} ${process.arch}`)
log.info(`Log file: ${getLogPath()}`)
log.info(`User data: ${app.getPath("userData")}`)
log.info(`isDev: ${isDev}`)

// ── Global error capture ──────────────────────────────────────────────────────
process.on("uncaughtException", err => {
    log.error("Uncaught exception in main process", err)
})
process.on("unhandledRejection", (reason) => {
    log.error("Unhandled promise rejection in main process", reason instanceof Error ? reason : new Error(String(reason)))
})

// ── Helpers ───────────────────────────────────────────────────────────────────
function createSplash() {
    log.info("Creating splash window")
    splash = new BrowserWindow({
        width:  400,
        height: 220,
        frame:  false,
        center: true,
        resizable:       false,
        alwaysOnTop:     true,
        backgroundColor: "#223742",
        webPreferences:  { nodeIntegration: false },
    })
    splash.loadFile(path.join(__dirname, "splash.html"))
    splash.webContents.on("did-fail-load", (_e, code, desc) => {
        log.error(`Splash failed to load: ${desc} (${code})`)
    })
}

const WAIT_TIMEOUT_MS = 60_000

async function waitForAri(endpoint) {
    log.info(`Waiting for ARI server at ${endpoint}/api/threads (timeout ${WAIT_TIMEOUT_MS / 1000}s) …`)
    const deadline = Date.now() + WAIT_TIMEOUT_MS
    let attempt = 0
    while (true) {
        attempt++
        try {
            const res = await fetch(`${endpoint}/api/threads`)
            log.info(`Health check attempt ${attempt}: HTTP ${res.status}`)
            if (res.status < 500) {
                // Any non-5xx means the ARI server (or its auth layer) responded — it's up
                log.info("ARI server is ready")
                return
            }
            // 5xx — could be Cloudflare 530 (origin offline), 502, 503, etc. — keep waiting
            log.info(`HTTP ${res.status} — origin not yet reachable, retrying…`)
        } catch (err) {
            if (attempt === 1 || attempt % 5 === 0)
                log.info(`Health check attempt ${attempt}: connection refused — ARI not up yet`)
        }

        if (Date.now() >= deadline) {
            log.warn(`ARI server did not become reachable after ${WAIT_TIMEOUT_MS / 1000}s — loading anyway`)
            return
        }
        await new Promise(r => setTimeout(r, 1000))
    }
}

async function restoreAuthCookie(endpoint) {
    const saved = store.get("savedAuthCookie")
    if (!saved) { log.info("No saved auth cookie to restore"); return }
    log.info("Restoring saved auth cookie")
    try {
        const url     = new URL(endpoint)
        const base    = `${url.protocol}//${url.host}`
        const isHttps = url.protocol === "https:"
        await session.defaultSession.cookies.set({
            url,
            name:           AUTH_COOKIE,
            value:          saved.value,
            httpOnly:       true,
            secure:         isHttps,
            expirationDate: saved.expirationDate,
            sameSite:       "lax",
        })
        log.info("Auth cookie restored successfully")
    } catch (err) {
        log.error("Failed to restore auth cookie", err)
    }
}

async function saveAuthCookie(endpoint) {
    try {
        const url     = new URL(endpoint)
        const cookies = await session.defaultSession.cookies.get({ url: endpoint, name: AUTH_COOKIE })
        if (cookies.length > 0) {
            store.set("savedAuthCookie", {
                value:          cookies[0].value,
                expirationDate: cookies[0].expirationDate,
            })
            log.info("Auth cookie saved")
        }
    } catch (err) {
        log.error("Failed to save auth cookie", err)
    }
}

async function createWindow() {
    const endpoint = isDev ? "http://localhost:5074" : store.get("endpoint", "https://a-r-i.ai")
    log.info(`Endpoint: ${endpoint}`)

    createSplash()
    await restoreAuthCookie(endpoint)
    await waitForAri(endpoint)

    log.info("Creating main window")
    win = new BrowserWindow({
        width:       1280,
        height:      800,
        minWidth:    800,
        minHeight:   600,
        titleBarStyle: "hidden",
        trafficLightPosition: { x: 12, y: 16 },
        show: false,
        webPreferences: {
            preload:          path.join(__dirname, "preload.js"),
            contextIsolation: true,
            nodeIntegration:  false,
        },
    })

    // ── Renderer process diagnostics ──────────────────────────────────────────
    const wlog = makeLogger("ARI.Renderer")

    win.webContents.on("did-start-loading",  () => wlog.info(`Loading ${endpoint} …`))
    win.webContents.on("did-finish-load",    () => wlog.info("Page loaded successfully"))
    win.webContents.on("did-fail-load", (_e, code, desc, url) => {
        wlog.error(`Page failed to load: ${desc} (${code}) — ${url}`)
    })
    win.webContents.on("render-process-gone", (_e, details) => {
        wlog.error(`Renderer process gone: reason=${details.reason}  exitCode=${details.exitCode}`)
    })
    win.webContents.on("unresponsive", () => wlog.warn("Renderer process is unresponsive"))
    win.webContents.on("responsive",   () => wlog.info("Renderer process is responsive again"))
    win.webContents.on("console-message", (_e, level, message, line, sourceId) => {
        // level: 0=verbose 1=info 2=warning 3=error
        if (level >= 2) {
            const label = level === 3 ? "console.error" : "console.warn"
            wlog.warn(`[${label}] ${message}  (${sourceId}:${line})`)
        }
    })

    log.info(`Loading URL: ${endpoint}`)
    win.loadURL(endpoint)

    win.webContents.on("did-finish-load", () => saveAuthCookie(endpoint))

    // Show window and dismiss splash only once the React app signals it's fully ready
    appReady.then(() => {
        log.info("App signalled ready — showing main window and closing splash")
        win.show()
        if (splash && !splash.isDestroyed()) {
            splash.destroy()
            splash = null
        }
    })

    // Fallback: show after 15 s if the app never signals ready
    setTimeout(() => {
        log.warn("App never signalled ready after 15 s — showing window anyway (fallback)")
        appReadyResolve()
    }, 15_000)
}

app.whenReady().then(() => {
    log.info("Electron app ready")
    createWindow()
    app.on("activate", () => {
        if (BrowserWindow.getAllWindows().filter(w => w !== splash).length === 0) {
            log.info("Re-creating window on activate")
            createWindow()
        }
    })
})

app.on("window-all-closed", () => {
    log.info("All windows closed")
    if (process.platform !== "darwin") {
        log.info("Quitting (non-macOS)")
        app.quit()
    }
})

// ── IPC: filesystem ───────────────────────────────────────
ipcMain.handle("fs:read", (_e, root, filePath) => {
    log.info(`fs:read  root=${root}  path=${filePath}`)
    return readFile(root, filePath)
})

ipcMain.handle("fs:write", (_e, root, filePath, content) => {
    log.info(`fs:write  root=${root}  path=${filePath}  bytes=${content?.length ?? 0}`)
    return writeFile(root, filePath, content)
})

ipcMain.handle("fs:pick-folder", async () => {
    log.info("fs:pick-folder  opening dialog")
    const result = await dialog.showOpenDialog(win, {
        properties: ["openDirectory"],
        title:      "Select project folder",
    })
    if (result.canceled) { log.info("fs:pick-folder  cancelled"); return null }
    log.info(`fs:pick-folder  selected: ${result.filePaths[0]}`)
    return result.filePaths[0]
})

ipcMain.handle("fs:tree", (_e, root) => {
    log.info(`fs:tree  root=${root}`)
    return getFileTree(root)
})

ipcMain.handle("fs:list-dir", (_e, root, dirPath) => {
    log.info(`fs:list-dir  root=${root}  path=${dirPath ?? "."}`)
    return listDirectory(root, dirPath)
})

ipcMain.handle("fs:search", (_e, root, pattern, searchPath, glob) => {
    log.info(`fs:search  root=${root}  pattern=${pattern}  path=${searchPath ?? "."}  glob=${glob ?? "*"}`)
    return searchFiles(root, pattern, searchPath, glob)
})

ipcMain.handle("fs:edit", (_e, root, filePath, oldString, newString) => {
    log.info(`fs:edit  root=${root}  path=${filePath}`)
    return editFile(root, filePath, oldString, newString)
})

// ── IPC: config ───────────────────────────────────────────
ipcMain.handle("cfg:get-endpoint", () => {
    const ep = store.get("endpoint", "")
    log.info(`cfg:get-endpoint → "${ep}"`)
    return ep
})
ipcMain.handle("cfg:set-endpoint", (_e, url) => {
    log.info(`cfg:set-endpoint → "${url}"`)
    store.set("endpoint", url)
})

// ── IPC: project local paths (stored per-machine, not on server) ──────────────
ipcMain.handle("project:get-path", (_e, projectId) => {
    const path = store.get(`projectPaths.${projectId}`, null)
    log.info(`project:get-path  id=${projectId} → ${path ?? "null"}`)
    return path
})
ipcMain.handle("project:set-path", (_e, projectId, path) => {
    if (path === null || path === undefined) {
        store.delete(`projectPaths.${projectId}`)
        log.info(`project:set-path  id=${projectId} → cleared`)
    } else {
        store.set(`projectPaths.${projectId}`, path)
        log.info(`project:set-path  id=${projectId} → ${path}`)
    }
})

// ── IPC: window controls ──────────────────────────────────
ipcMain.handle("window:close", () => {
    log.info("IPC window:close received")
    win?.close()
})

ipcMain.handle("app:ready", () => {
    log.info("IPC app:ready received")
    appReadyResolve()
})

ipcMain.handle("window:move-by", (_e, dx, dy) => {
    const [x, y] = win.getPosition()
    win.setPosition(x + Math.round(dx), y + Math.round(dy))
})
