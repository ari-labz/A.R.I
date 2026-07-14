const { app, BrowserWindow, ipcMain, shell } = require("electron")
const https  = require("https")
const http   = require("http")
const fs     = require("fs")
const path   = require("path")
const os     = require("os")
const { execFile, spawn } = require("child_process")

const OWNER = "ari-labz"
const REPO  = "A.R.I"

// The GitHub API needs a token only while the repo is private. Set this to false when the
// repo goes public — the installer then fetches releases anonymously and never asks for a token.
const REPO_PRIVATE = true

// ── Paths ─────────────────────────────────────────────────────────────────────
// Server versions live under {base}/server/{version}. The desktop installer
// keeps its own versions flat under {base}/{version}, so the two never collide.

function getBaseDir() {
    if (process.platform === "win32")
        return path.join(process.env.LOCALAPPDATA || os.homedir(), "ARI")
    if (process.platform === "darwin")
        return path.join(os.homedir(), "Library", "Application Support", "ARI")
    return path.join(os.homedir(), ".local", "share", "ARI")
}

const baseDir     = getBaseDir()
const serverDir   = path.join(baseDir, "server")
const tokenFile   = path.join(baseDir, "github_token.txt")
const currentFile = path.join(serverDir, "current.txt")
const cacheFile   = path.join(baseDir, "protocol-cache.json")
fs.mkdirSync(serverDir, { recursive: true })

// ── Window ────────────────────────────────────────────────────────────────────

let win

app.whenReady().then(() => {
    win = new BrowserWindow({
        width: 460,
        height: 440,
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

// ── IPC: token ────────────────────────────────────────────────────────────────

ipcMain.handle("get-platform", () => process.platform)

ipcMain.handle("needs-token", () => REPO_PRIVATE)

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

// ── IPC: releases + protocol ────────────────────────────────────────────────────

ipcMain.handle("fetch-releases", async (_, token) => {
    const releases = await ghJson(token, `/repos/${OWNER}/${REPO}/releases?per_page=100`)
    if (!Array.isArray(releases) || releases.length === 0)
        throw new Error("No releases found on GitHub.")

    const list = releases
        .filter(r => !r.draft)
        .sort((a, b) => compareVersions(b.tag_name, a.tag_name))
        .map(r => ({
            tagName:    r.tag_name,
            version:    r.tag_name.replace(/^v/i, ""),
            prerelease: r.prerelease,
            assets:     r.assets.map(a => ({ id: a.id, name: a.name })),
        }))

    // Resolve each version's protocol (cached — a tag's manifest never changes).
    const cache = readCache()
    await Promise.all(list.map(async r => {
        if (cache[r.tagName] === undefined)
            cache[r.tagName] = await fetchProtocol(token, r.tagName)
        r.protocol = cache[r.tagName]
    }))
    writeCache(cache)

    return list
})

// Reads manifest.json ({ "version": "..", "protocol": N }) committed at the tag.
async function fetchProtocol(token, tag) {
    try {
        const res = await ghJson(token, `/repos/${OWNER}/${REPO}/contents/manifest.json?ref=${encodeURIComponent(tag)}`)
        if (!res?.content) return null
        const json = JSON.parse(Buffer.from(res.content, "base64").toString("utf8"))
        return Number.isInteger(json.protocol) ? json.protocol : null
    } catch {
        return null   // older releases may predate the manifest
    }
}

// ── IPC: installed state ────────────────────────────────────────────────────────

ipcMain.handle("installed-info", () => {
    const version = installedVersion()
    if (!version) return null
    return { version, protocol: bundledProtocol(version) }
})

function installedVersion() {
    let version = null
    if (fs.existsSync(currentFile)) {
        const v = fs.readFileSync(currentFile, "utf8").trim()
        if (v && fs.existsSync(path.join(serverDir, v))) version = v
    }
    if (!version) {
        const dirs = installedVersionDirs()
        if (dirs.length) version = dirs[0].name
    }
    return version
}

function installedVersionDirs() {
    if (!fs.existsSync(serverDir)) return []
    return fs.readdirSync(serverDir)
        .map(n => ({ name: n, full: path.join(serverDir, n) }))
        .filter(d => /^\d+\.\d+\.\d+/.test(d.name) && fs.statSync(d.full).isDirectory())
        .sort((a, b) => compareVersions(b.name, a.name))
}

// Reads the manifest.json bundled inside an installed version dir.
function bundledProtocol(version) {
    try {
        const p = path.join(serverDir, version, "manifest.json")
        if (!fs.existsSync(p)) return null
        const json = JSON.parse(fs.readFileSync(p, "utf8"))
        return Number.isInteger(json.protocol) ? json.protocol : null
    } catch {
        return null
    }
}

// ── IPC: install + launch ────────────────────────────────────────────────────────

ipcMain.handle("download-and-install", async (event, token, release, options) => {
    const ver       = release.version
    const assetName = getAssetName(ver)
    const asset     = release.assets.find(a => a.name === assetName)
    if (!asset) {
        const names = release.assets.map(a => a.name).join(", ")
        throw new Error(`Asset "${assetName}" not in release. Available: ${names || "none"}`)
    }

    const zipPath    = path.join(os.tmpdir(), asset.name)
    const versionDir = path.join(serverDir, ver)
    fs.mkdirSync(versionDir, { recursive: true })

    await downloadAsset(token, asset.id, zipPath, (pct, received, total) => {
        event.sender.send("download-progress", { pct, received, total })
    })

    event.sender.send("status", "Extracting…")
    await extract(zipPath, versionDir)
    fs.rmSync(zipPath, { force: true })
    if (process.platform !== "win32") setExecutableBit(versionDir)

    // Record the protocol locally so installed-info never depends on the zip's
    // contents. Prefer a manifest shipped in the release; fall back to the value
    // resolved from GitHub when the list was fetched.
    if (!fs.existsSync(path.join(versionDir, "manifest.json")) && Number.isInteger(release.protocol)) {
        const manifest = { version: ver, protocol: release.protocol }
        fs.writeFileSync(path.join(versionDir, "manifest.json"), JSON.stringify(manifest), "utf8")
    }

    fs.writeFileSync(currentFile, ver, "utf8")
    cleanOldVersions(ver)

    if (options?.addShortcut) {
        event.sender.send("status", "Creating shortcut…")
        try { addShortcut(versionDir) } catch (e) { /* best-effort */ }
    }
    if (options?.startServer) {
        event.sender.send("status", "Starting server…")
        try { launch(versionDir) } catch (e) { /* best-effort */ }
    }
    return { version: ver, protocol: bundledProtocol(ver) }
})

ipcMain.handle("launch-server", (_, version) => {
    const dir = version ? path.join(serverDir, version) : latestInstalledDir()
    if (!dir || !fs.existsSync(dir)) throw new Error("No installed version to launch.")
    launch(dir)
})

function latestInstalledDir() {
    const v = installedVersion()
    return v ? path.join(serverDir, v) : null
}

function launch(versionDir) {
    const exe = findExecutable(versionDir)
    if (!exe) throw new Error(`Could not find the ARI server executable in ${versionDir}`)
    if (process.platform === "darwin" && exe.endsWith(".app")) {
        spawn("open", [exe], { detached: true }).unref()
    } else {
        spawn(exe, [], { detached: true, stdio: "ignore" }).unref()
    }
}

// ── Helpers: GitHub ─────────────────────────────────────────────────────────────

function ghHeaders(token, accept) {
    const headers = {
        "User-Agent":           "ARIInstaller/1.0",
        "X-GitHub-Api-Version": "2022-11-28",
        "Accept":               accept || "application/vnd.github+json",
    }
    if (token) headers["Authorization"] = `token ${token}`
    return headers
}

function ghJson(token, apiPath) {
    return new Promise((resolve, reject) => {
        https.get({ hostname: "api.github.com", path: apiPath, headers: ghHeaders(token) }, res => {
            let data = ""
            res.on("data", c => data += c)
            res.on("end", () => {
                if (res.statusCode === 401 || res.statusCode === 403) return reject(new Error("TOKEN_INVALID"))
                if (res.statusCode === 404) return reject(new Error("NOT_FOUND"))
                if (res.statusCode !== 200) return reject(new Error(`GitHub API returned ${res.statusCode}`))
                try { resolve(JSON.parse(data)) }
                catch { reject(new Error("Failed to parse GitHub response.")) }
            })
        }).on("error", reject)
    })
}

function downloadAsset(token, assetId, destPath, onProgress) {
    return new Promise((resolve, reject) => {
        const opts = {
            hostname: "api.github.com",
            path: `/repos/${OWNER}/${REPO}/releases/assets/${assetId}`,
            headers: ghHeaders(token, "application/octet-stream"),
        }
        function doGet(url, redirectCount = 0) {
            if (redirectCount > 5) return reject(new Error("Too many redirects"))
            const mod     = url?.startsWith("http://") ? http : https
            const reqOpts = url ? new URL(url) : opts
            mod.get(reqOpts, res => {
                if ([301, 302, 307, 308].includes(res.statusCode))
                    return doGet(res.headers.location, redirectCount + 1)
                if (res.statusCode !== 200)
                    return reject(new Error(`Download failed: ${res.statusCode}`))
                const total  = parseInt(res.headers["content-length"] || "0", 10)
                let received = 0
                const dest   = fs.createWriteStream(destPath)
                res.on("data", chunk => {
                    received += chunk.length
                    dest.write(chunk)
                    if (total > 0) onProgress(Math.floor(received * 100 / total), received, total)
                })
                res.on("end",   () => { dest.end(); resolve() })
                res.on("error", e  => { dest.destroy(); reject(e) })
            }).on("error", reject)
        }
        doGet(null)
    })
}

// ── Helpers: filesystem / versions ───────────────────────────────────────────────

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

function extract(zipPath, destDir) {
    return new Promise((resolve, reject) => {
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
        if (name === "ARI.Core" || name === "ARI" || name === "ari" || !path.extname(f)) {
            try { fs.chmodSync(f, 0o755) } catch {}
        }
    }
}

function findExecutable(dir) {
    if (process.platform === "win32")
        return walkFiles(dir).find(f => /ARI(\.Core)?\.exe$/i.test(f))
    if (process.platform === "darwin") {
        const apps = walkDirs(dir).filter(d => d.endsWith(".app"))
        if (apps.length) return apps[0]
    }
    return walkFiles(dir).find(f => ["ARI.Core", "ARI", "ari"].includes(path.basename(f)))
}

function cleanOldVersions(keepVersion) {
    // Keep the 3 newest installed versions so downgrade stays fast.
    const dirs = installedVersionDirs()
    for (const d of dirs.slice(3)) {
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

function readCache() {
    try { return JSON.parse(fs.readFileSync(cacheFile, "utf8")) } catch { return {} }
}
function writeCache(cache) {
    try { fs.writeFileSync(cacheFile, JSON.stringify(cache), "utf8") } catch {}
}

// ── Helpers: OS shortcuts ────────────────────────────────────────────────────────

function addShortcut(versionDir) {
    const exe = findExecutable(versionDir)
    if (!exe) return
    if (process.platform === "darwin")      addToDock(exe)
    else if (process.platform === "win32")  addToStartMenu(exe)
}

// macOS: add a .app to the Dock's persistent-apps and reload the Dock.
function addToDock(appPath) {
    if (!appPath.endsWith(".app")) return
    const item = `<dict><key>tile-data</key><dict><key>file-data</key><dict>` +
        `<key>_CFURLString</key><string>${appPath}</string>` +
        `<key>_CFURLStringType</key><integer>0</integer></dict></dict></dict>`
    execFile("defaults", ["write", "com.apple.dock", "persistent-apps", "-array-add", item], () => {
        execFile("killall", ["Dock"], () => {})
    })
}

// Windows: drop a .lnk in the Start Menu Programs folder.
function addToStartMenu(exePath) {
    const programs = path.join(process.env.APPDATA || os.homedir(), "Microsoft", "Windows", "Start Menu", "Programs")
    const lnk = path.join(programs, "ARI Server.lnk")
    const ps = [
        `$s = (New-Object -COM WScript.Shell).CreateShortcut('${lnk}');`,
        `$s.TargetPath = '${exePath}';`,
        `$s.WorkingDirectory = '${path.dirname(exePath)}';`,
        `$s.Save()`,
    ].join(" ")
    execFile("powershell.exe", ["-NoProfile", "-Command", ps], () => {})
}
