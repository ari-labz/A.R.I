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

// App releases are tagged ARI_Server_v<ver> (pre-releases). The installer's own releases are
// ARI_Server_Installer_v<ver>, excluded because they don't start with this prefix.
const APP_PREFIX = "ARI_Server_v"
const verFromTag = tag => tag.slice(APP_PREFIX.length)

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
// macOS launch point: a thin app in /Applications that runs the active user-space version.
const LAUNCHER_APP = "/Applications/A.R.I.app"
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
    // A token the user saved here wins over an ambient GITHUB_TOKEN env var, so re-entering a
    // valid token sticks even on a machine where the env var holds a stale/wrong one.
    if (fs.existsSync(tokenFile)) {
        const t = fs.readFileSync(tokenFile, "utf8").trim()
        if (t) return t
    }
    const env = process.env.GITHUB_TOKEN
    if (env?.trim()) return env.trim()
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
        .filter(r => !r.draft && r.tag_name.startsWith(APP_PREFIX))
        .sort((a, b) => compareVersions(verFromTag(b.tag_name), verFromTag(a.tag_name)))
        .map(r => ({
            tagName:    r.tag_name,
            version:    verFromTag(r.tag_name),
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
        event.sender.send("status", "Adding to Applications…")
        try { await addShortcut(versionDir) } catch (e) { /* best-effort */ }
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
    // The server reads its BuildPath (wwwroot, StyleTTS2, manifest) from APP_INSTALL_ROOT;
    // point it at the version dir it was installed to, else it looks in the OS default.
    const opts = { detached: true, stdio: "ignore", env: { ...process.env, APP_INSTALL_ROOT: versionDir } }
    if (process.platform === "darwin" && exe.endsWith(".app")) {
        spawn("open", [exe], opts).unref()
    } else {
        spawn(exe, [], opts).unref()
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
    if (process.platform === "win32")  return `ARI_Server_v${version}_win.zip`
    if (process.platform === "darwin") return `ARI_Server_v${version}_mac.zip`
    return `ARI_Server_v${version}_linux.zip`
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

async function addShortcut(versionDir) {
    if (process.platform === "darwin")      await createMacLauncher()
    else if (process.platform === "win32")  addToStartMenu(createWinLauncher())
}

// macOS: a thin launcher app in /Applications that runs whichever server version is current.
// Version-agnostic (reads current.txt at launch), so it's created once; switching versions never
// touches it. Writing to /Applications needs admin, hence the one-time authorization prompt.
function createMacLauncher() {
    if (fs.existsSync(LAUNCHER_APP)) return Promise.resolve()   // already installed

    const tmp = path.join(os.tmpdir(), "A.R.I.app")
    fs.rmSync(tmp, { recursive: true, force: true })
    fs.mkdirSync(path.join(tmp, "Contents", "MacOS"), { recursive: true })
    fs.mkdirSync(path.join(tmp, "Contents", "Resources"), { recursive: true })

    fs.writeFileSync(path.join(tmp, "Contents", "Info.plist"),
        `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>A.R.I</string>
  <key>CFBundleDisplayName</key><string>A.R.I</string>
  <key>CFBundleIdentifier</key><string>ai.ari.server</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>ari-launch</string>
  <key>CFBundleIconFile</key><string>icon</string>
</dict></plist>`)

    const runner = path.join(tmp, "Contents", "MacOS", "ari-launch")
    fs.writeFileSync(runner,
        `#!/bin/bash
BASE="$HOME/Library/Application Support/ARI/server"
VER="$(cat "$BASE/current.txt" 2>/dev/null)"
DIR="$BASE/$VER"
if [ -z "$VER" ] || [ ! -x "$DIR/ARI.Core" ]; then
  osascript -e 'display alert "A·R·I" message "No installed server version found. Open the A·R·I installer first."'
  exit 1
fi
export APP_INSTALL_ROOT="$DIR"
exec "$DIR/ARI.Core"
`)
    fs.chmodSync(runner, 0o755)

    const icon = path.join(__dirname, "..", "assets", "icon.icns")
    if (fs.existsSync(icon)) fs.copyFileSync(icon, path.join(tmp, "Contents", "Resources", "icon.icns"))

    // Move into /Applications with a single admin authorization.
    return new Promise(resolve => {
        const shell = `rm -rf '${LAUNCHER_APP}' && cp -R '${tmp}' '${LAUNCHER_APP}'`
        const osa   = `do shell script "${shell.replace(/"/g, '\\"')}" with administrator privileges`
        execFile("osascript", ["-e", osa], () => { fs.rmSync(tmp, { recursive: true, force: true }); resolve() })
    })
}

// Windows: a small launcher .cmd (reads current.txt, sets APP_INSTALL_ROOT) plus a Start Menu
// shortcut pointing at it — so the shortcut survives version switches and starts the server right.
function createWinLauncher() {
    const cmd = path.join(baseDir, "ari-launch.cmd")
    fs.writeFileSync(cmd,
        `@echo off\r\n` +
        `set "BASE=${serverDir}"\r\n` +
        `set /p VER=<"%BASE%\\current.txt"\r\n` +
        `set "APP_INSTALL_ROOT=%BASE%\\%VER%"\r\n` +
        `start "" "%APP_INSTALL_ROOT%\\ARI.Core.exe"\r\n`)
    return cmd
}

function addToStartMenu(target) {
    const programs = path.join(process.env.APPDATA || os.homedir(), "Microsoft", "Windows", "Start Menu", "Programs")
    const lnk = path.join(programs, "A.R.I.lnk")
    const ps = [
        `$s = (New-Object -COM WScript.Shell).CreateShortcut('${lnk}');`,
        `$s.TargetPath = '${target}';`,
        `$s.WorkingDirectory = '${path.dirname(target)}';`,
        `$s.Save()`,
    ].join(" ")
    execFile("powershell.exe", ["-NoProfile", "-Command", ps], () => {})
}
