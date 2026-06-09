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
const { readFile, writeFile, getFileTree } = require("./fs")

const store = new Store()
const isDev = process.env.NODE_ENV === "development"
const AUTH_COOKIE = ".AspNetCore.Cookies"

let win
let splash
let appReadyResolve
const appReady = new Promise(resolve => { appReadyResolve = resolve })

function createSplash() {
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
}

async function waitForAri(endpoint) {
    while (true) {
        try {
            const res = await fetch(`${endpoint}/api/threads`)
            if (res.status !== 503) return
        } catch { /* connection refused — ARI not up yet */ }
        await new Promise(r => setTimeout(r, 1000))
    }
}

async function restoreAuthCookie(endpoint) {
    const saved = store.get("savedAuthCookie")
    if (!saved) return
    try {
        const url  = new URL(endpoint)
        const base = `${url.protocol}//${url.host}`
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
    } catch { /* best-effort */ }
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
        }
    } catch { /* best-effort */ }
}

async function createWindow() {
    const endpoint = isDev ? "http://localhost:5074" : store.get("endpoint", "https://a-r-i.ai")

    createSplash()
    await restoreAuthCookie(endpoint)
    await waitForAri(endpoint)

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

    win.loadURL(endpoint)

    win.webContents.on("did-finish-load", () => saveAuthCookie(endpoint))

    // Show window and dismiss splash only once the React app signals it's fully ready
    appReady.then(() => {
        win.show()
        if (splash && !splash.isDestroyed()) {
            splash.destroy()
            splash = null
        }
    })

    // Fallback: if the app never signals ready (e.g. auth redirect loop), show after 15s
    setTimeout(() => appReadyResolve(), 15_000)
}

app.whenReady().then(() => {
    createWindow()
    app.on("activate", () => { if (BrowserWindow.getAllWindows().filter(w => w !== splash).length === 0) createWindow() })
})

app.on("window-all-closed", () => { if (process.platform !== "darwin") app.quit() })

// ── IPC: filesystem ───────────────────────────────────────
ipcMain.handle("fs:read", (_e, root, filePath) => readFile(root, filePath))

ipcMain.handle("fs:write", (_e, root, filePath, content) => writeFile(root, filePath, content))

ipcMain.handle("fs:pick-folder", async () => {
    const result = await dialog.showOpenDialog(win, {
        properties: ["openDirectory"],
        title:      "Select project folder",
    })
    return result.canceled ? null : result.filePaths[0]
})

ipcMain.handle("fs:tree", (_e, root) => getFileTree(root))

// ── IPC: config ───────────────────────────────────────────
ipcMain.handle("cfg:get-endpoint",  ()        => store.get("endpoint", ""))
ipcMain.handle("cfg:set-endpoint",  (_e, url) => store.set("endpoint", url))

// ── IPC: window movement ──────────────────────────────────
ipcMain.handle("app:ready", () => appReadyResolve())

ipcMain.handle("window:move-by", (_e, dx, dy) => {
    const [x, y] = win.getPosition()
    win.setPosition(x + Math.round(dx), y + Math.round(dy))
})
