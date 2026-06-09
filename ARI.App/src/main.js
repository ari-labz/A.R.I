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
const { app, BrowserWindow, ipcMain, dialog } = require("electron")
const Store = require("electron-store")
const { readFile, writeFile, getFileTree } = require("./fs")

const store = new Store()
const isDev = process.env.NODE_ENV === "development"

let win
let splash

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

async function createWindow() {
    const endpoint = isDev ? "http://localhost:5074" : store.get("endpoint", "http://localhost:5074")

    createSplash()
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

    win.once("ready-to-show", () => {
        if (splash && !splash.isDestroyed()) {
            splash.destroy()
            splash = null
        }
        win.show()
    })
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
ipcMain.handle("window:move-by", (_e, dx, dy) => {
    const [x, y] = win.getPosition()
    win.setPosition(x + Math.round(dx), y + Math.round(dy))
})
