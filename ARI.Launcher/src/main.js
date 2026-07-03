const { app, BrowserWindow, ipcMain, shell } = require("electron")
const https  = require("https")
const http   = require("http")
const fs     = require("fs")
const path   = require("path")
const os     = require("os")
const { execFile, spawn } = require("child_process")

const OWNER = "Xywren"
const REPO  = "A.R.I"

// ── Paths ─────────────────────────────────────────────────────────────────────

function getBaseDir() {
    if (process.platform === "win32")
        return path.join(process.env.LOCALAPPDATA || os.homedir(), "ARI")
    if (process.platform === "darwin")
        return path.join(os.homedir(), "Library", "Application Support", "ARI")
    return path.join(os.homedir(), ".local", "share", "ARI")
}

const baseDir   = getBaseDir()
const tokenFile = path.join(baseDir, "github_token.txt")
fs.mkdirSync(baseDir, { recursive: true })

// ── Window ────────────────────────────────────────────────────────────────────

let win

app.whenReady().then(() => {
    win = new BrowserWindow({
        width: 420,
        height: 340,
        resizable: false,
        titleBarStyle: "hidden",
        trafficLightPosition: { x: 16, y: 16 },
        backgroundColor: "#1b2e38",
        icon: path.join(__dirname, "..", "assets", "icon.png"),
        webPreferences: {
            preload: path.join(__dirname, "preload.js"),
            contextIsolation: true,
        },
    })
    win.loadFile(path.join(__dirname, "index.html"))
})

app.on("window-all-closed", () => app.quit())

// ── IPC handlers ──────────────────────────────────────────────────────────────

ipcMain.handle("get-token", () => {
    const env = process.env.GITHUB_TOKEN
    if (env?.trim()) return env.trim()
    if (fs.existsSync(tokenFile)) {
        const t = fs.readFileSync(tokenFile, "utf8").trim()
        if (t) return t
    }
    return null
})

ipcMain.handle("save-token", (_, token) => {
    fs.writeFileSync(tokenFile, token.trim(), "utf8")
})

ipcMain.handle("fetch-release", async (_, token) => {
    return new Promise((resolve, reject) => {
        const opts = {
            hostname: "api.github.com",
            path: `/repos/${OWNER}/${REPO}/releases?per_page=100`,
            headers: {
                "User-Agent":           "ARILauncher/1.0",
                "Authorization":        `token ${token}`,
                "X-GitHub-Api-Version": "2022-11-28",
                "Accept":               "application/vnd.github+json",
            },
        }
        https.get(opts, res => {
            let data = ""
            res.on("data", c => data += c)
            res.on("end", () => {
                if (res.statusCode === 401 || res.statusCode === 403)
                    return reject(new Error("TOKEN_INVALID"))
                if (res.statusCode !== 200)
                    return reject(new Error(`GitHub API returned ${res.statusCode}`))
                try {
                    const releases = JSON.parse(data)
                    if (!Array.isArray(releases) || releases.length === 0)
                        return reject(new Error("No releases found on GitHub."))
                    // GitHub's list order is unreliable (sorts by tag ref date, not version),
                    // so pick the highest semver rather than releases[0].
                    const r = releases
                        .filter(x => !x.draft)
                        .sort((a, b) => compareVersions(b.tag_name, a.tag_name))[0]
                    resolve({ tagName: r.tag_name, assets: r.assets.map(a => ({ id: a.id, name: a.name })) })
                } catch (e) {
                    reject(new Error("Failed to parse GitHub response."))
                }
            })
        }).on("error", reject)
    })
})

ipcMain.handle("version-installed", (_, tagName) => {
    const ver = tagName.replace(/^v/, "")
    const dir = path.join(baseDir, ver)
    return fs.existsSync(dir) && fs.readdirSync(dir).length > 0
})

ipcMain.handle("download-and-install", async (event, token, release) => {
    const ver      = release.tagName.replace(/^v/, "")
    const assetName = getAssetName(ver)
    const asset    = release.assets.find(a => a.name === assetName)

    if (!asset) {
        const names = release.assets.map(a => a.name).join(", ")
        throw new Error(`Asset "${assetName}" not in release. Available: ${names || "none"}`)
    }

    const zipPath  = path.join(os.tmpdir(), asset.name)
    const versionDir = path.join(baseDir, ver)
    fs.mkdirSync(versionDir, { recursive: true })

    // Download via GitHub API (works for private repos)
    await downloadAsset(token, asset.id, zipPath, (pct, received, total) => {
        event.sender.send("download-progress", { pct, received, total })
    })

    event.sender.send("status", "Extracting...")
    await extract(zipPath, versionDir)
    fs.rmSync(zipPath, { force: true })

    if (process.platform !== "win32") setExecutableBit(versionDir)

    cleanOldVersions(ver)
    return versionDir
})

ipcMain.handle("launch-ari", (_, versionDirOrTag) => {
    // Accept either a full path or a version tag — find latest installed if null
    let versionDir = versionDirOrTag
    if (!versionDir) {
        const dirs = fs.readdirSync(baseDir)
            .map(n => ({ name: n, full: path.join(baseDir, n) }))
            .filter(d => fs.statSync(d.full).isDirectory() && /^\d+\.\d+\.\d+/.test(d.name))
            .sort((a, b) => b.name.localeCompare(a.name, undefined, { numeric: true }))
        if (dirs.length === 0) throw new Error("No installed version found.")
        versionDir = dirs[0].full
    }
    const exe = findExecutable(versionDir)
    if (!exe) throw new Error(`Could not find ARI executable in ${versionDir}`)
    if (process.platform === "darwin") {
        spawn("open", ["-a", exe.endsWith("/ARI") ? path.dirname(path.dirname(path.dirname(exe))) : exe], { detached: true }).unref()
    } else {
        spawn(exe, [], { detached: true, stdio: "ignore" }).unref()
    }
    setTimeout(() => app.quit(), 500)
})

// ── Helpers ───────────────────────────────────────────────────────────────────

// Compare "v0.4.0" vs "v0.3.4" numerically. Returns >0 if a is newer.
function compareVersions(a, b) {
    const parse = t => String(t).replace(/^v/i, "").split(/[.\-+]/).map(n => parseInt(n, 10) || 0)
    const pa = parse(a), pb = parse(b)
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const d = (pa[i] || 0) - (pb[i] || 0)
        if (d !== 0) return d
    }
    return 0
}

function getAssetName(version) {
    if (process.platform === "win32")  return `ARI-${version}-win.zip`
    if (process.platform === "darwin") return `ARI-${version}-mac.zip`
    return `ARI-${version}-linux.zip`
}

function downloadAsset(token, assetId, destPath, onProgress) {
    return new Promise((resolve, reject) => {
        const opts = {
            hostname: "api.github.com",
            path: `/repos/${OWNER}/${REPO}/releases/assets/${assetId}`,
            headers: {
                "User-Agent":           "ARILauncher/1.0",
                "Authorization":        `token ${token}`,
                "X-GitHub-Api-Version": "2022-11-28",
                "Accept":               "application/octet-stream",
            },
        }

        function doGet(url, redirectCount = 0) {
            if (redirectCount > 5) return reject(new Error("Too many redirects"))
            const mod = url?.startsWith("http://") ? http : https
            const reqOpts = url ? new URL(url) : opts
            const req = mod.get(reqOpts, res => {
                if (res.statusCode === 301 || res.statusCode === 302 || res.statusCode === 307 || res.statusCode === 308) {
                    return doGet(res.headers.location, redirectCount + 1)
                }
                if (res.statusCode !== 200) {
                    return reject(new Error(`Download failed: ${res.statusCode}`))
                }
                const total    = parseInt(res.headers["content-length"] || "0", 10)
                let   received = 0
                const dest     = fs.createWriteStream(destPath)
                res.on("data", chunk => {
                    received += chunk.length
                    dest.write(chunk)
                    if (total > 0) onProgress(Math.floor(received * 100 / total), received, total)
                })
                res.on("end",   () => { dest.end(); resolve() })
                res.on("error", e  => { dest.destroy(); reject(e) })
            })
            req.on("error", reject)
        }
        doGet(null)
    })
}

function extract(zipPath, destDir) {
    return new Promise((resolve, reject) => {
        // Use platform unzip / PowerShell
        if (process.platform === "win32") {
            const ps = `Expand-Archive -Path '${zipPath}' -DestinationPath '${destDir}' -Force`
            execFile("powershell.exe", ["-NoProfile", "-Command", ps], err => err ? reject(err) : resolve())
        } else {
            execFile("unzip", ["-o", zipPath, "-d", destDir], err => err ? reject(err) : resolve())
        }
    })
}

function setExecutableBit(dir) {
    for (const f of walkFiles(dir)) {
        const name = path.basename(f)
        if (name === "ARI" || name === "ari" || !path.extname(f)) {
            try { fs.chmodSync(f, 0o755) } catch {}
        }
    }
}

function findExecutable(dir) {
    if (process.platform === "win32") {
        return walkFiles(dir).find(f => f.endsWith(".exe"))
    }
    if (process.platform === "darwin") {
        const apps = walkDirs(dir).filter(d => d.endsWith(".app"))
        if (apps.length > 0) return path.join(apps[0], "Contents", "MacOS", "ARI")
    }
    return walkFiles(dir).find(f => path.basename(f) === "ARI" || path.basename(f) === "ari")
}

function cleanOldVersions(keepVersion) {
    const dirs = fs.readdirSync(baseDir)
        .map(n => ({ name: n, full: path.join(baseDir, n) }))
        .filter(d => fs.statSync(d.full).isDirectory() && /^\d+\.\d+\.\d+/.test(d.name))
        .sort((a, b) => b.name.localeCompare(a.name, undefined, { numeric: true }))
    for (const d of dirs.slice(2)) {
        if (d.name === keepVersion) continue
        try { fs.rmSync(d.full, { recursive: true, force: true }) } catch {}
    }
}

function* walkFiles(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name)
        if (entry.isDirectory()) yield* walkFiles(full)
        else yield full
    }
}

function* walkDirs(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name)
        if (entry.isDirectory()) { yield full; yield* walkDirs(full) }
    }
}
