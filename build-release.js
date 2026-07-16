// Builds the A·R·I server release artifacts, per platform:
//   ARI_Server_v{appVer}_{plat}.zip            — the server app: self-contained ARI.Core
//                                                 (bundled .NET runtime) + the ARI.Console window.
//   ARI_Server_Installer_v{instVer}_{plat}.zip — the installer app.
// The csproj copies wwwroot, External/StyleTTS2, the Listener scripts and manifest.json into the
// Core output. Python venvs are provisioned on first run, not bundled.
//
// Usage:  bun build-release.js   (node isn't installed; electron-builder runs via bunx)

const { execSync } = require("child_process")
const fs   = require("fs")
const path = require("path")

const appVersion       = JSON.parse(fs.readFileSync(path.join(__dirname, "manifest.json"), "utf8")).version
const installerVersion = require(path.join(__dirname, "ARI.Installer", "package.json")).version
const buildsDir    = path.join(__dirname, "Builds")
const versionDir   = path.join(buildsDir, `v${appVersion}`)
const csproj       = path.join(__dirname, "ARI.Core", "ARI.Core.csproj")
const consoleDir   = path.join(__dirname, "ARI.Console")
const installerDir = path.join(__dirname, "ARI.Installer")

fs.mkdirSync(versionDir, { recursive: true })

// BUILD_TARGET lets CI build just the app or just the installer, so pushing an app tag never
// rebuilds the installer (and vice versa). Unset / "all" = both (local default).
const buildTarget = process.env.BUILD_TARGET || "all"   // app | installer | all

// bun on PATH for the csproj BuildUI target and bunx electron-builder.
const env = { ...process.env, PATH: `${path.join(process.env.HOME || "", ".bun", "bin")}:${process.env.PATH}` }

for (const d of [consoleDir, installerDir]) {
    if (!fs.existsSync(path.join(d, "node_modules", ".bin", "electron-builder"))) {
        console.log(`\n── Installing deps in ${path.basename(d)}\n`)
        execSync("bun install", { stdio: "inherit", cwd: d, env })
    }
}

// BUILD_PLATFORM (space/comma list) restricts which OS artifacts are built, so CI can split mac
// onto a macOS runner and win+linux onto a Linux runner. Unset = all (local default).
const wantedPlats = (process.env.BUILD_PLATFORM || "mac win linux").split(/[\s,]+/).filter(Boolean)

const targets = [
    { rid: "osx-arm64", plat: "mac",   eb: "--mac" },
    { rid: "win-x64",   plat: "win",   eb: "--win --x64" },
    { rid: "linux-x64", plat: "linux", eb: "--linux --x64" },
].filter(t => wantedPlats.includes(t.plat))

// Recursively find electron-builder's output: a .app (mac) or a *-unpacked dir (win/linux).
function findConsoleBuild(dir) {
    const stack = [dir]
    while (stack.length) {
        const d = stack.pop()
        for (const e of fs.readdirSync(d, { withFileTypes: true })) {
            if (!e.isDirectory()) continue
            if (e.name.endsWith(".app") || e.name.endsWith("-unpacked")) return path.join(d, e.name)
            stack.push(path.join(d, e.name))
        }
    }
    return null
}

// Build the Console (unpacked) and drop it into the server publish dir: mac → A.R.I.app at the
// root; win/linux → a Console/ folder holding the unpacked app.
function bundleConsole(target, pubDir) {
    const out = path.join(versionDir, `.console-${target.rid}`)
    fs.rmSync(out, { recursive: true, force: true })
    console.log(`\n── Building Console (${target.plat})\n`)
    execSync(`bunx electron-builder ${target.eb} --dir --config.directories.output="${out}"`,
        { stdio: "inherit", cwd: consoleDir, env })
    const built = findConsoleBuild(out)
    if (!built) throw new Error(`Console build produced nothing for ${target.plat}`)
    const dest = built.endsWith(".app")
        ? path.join(pubDir, path.basename(built))
        : path.join(pubDir, "Console")
    execSync(`cp -R "${built}" "${dest}"`)
    fs.rmSync(out, { recursive: true, force: true })
}

// ── 1. Server app: Core + Console ────────────────────────────────────────────────
if (buildTarget !== "installer")
for (const target of targets) {
    const zip      = `ARI_Server_v${appVersion}_${target.plat}.zip`
    const stage    = path.join(versionDir, `.stage-${target.rid}`)
    const buildDir = path.join(stage, "build")
    const pubDir   = path.join(stage, "publish")
    const outZip   = path.join(versionDir, zip)

    fs.rmSync(stage, { recursive: true, force: true })
    fs.mkdirSync(pubDir, { recursive: true })
    fs.rmSync(outZip, { force: true })

    console.log(`\n══ Server app ${target.plat} ══\n── Publishing ARI.Core (${target.rid})\n`)
    execSync(
        `dotnet publish "${csproj}" -c Release -r ${target.rid} --self-contained true ` +
        `-p:AppInstallRoot="${buildDir}" -o "${pubDir}"`,
        { stdio: "inherit", env },
    )

    bundleConsole(target, pubDir)

    console.log(`\n── Zipping ${zip}\n`)
    // -y preserves symlinks (the .app bundle contains them).
    execSync(`zip -r -y -q "${outZip}" .`, { stdio: "inherit", cwd: pubDir })
    fs.rmSync(stage, { recursive: true, force: true })
}

// ── 2. Server installer ──────────────────────────────────────────────────────────
if (buildTarget !== "app")
for (const target of targets) {
    const out = path.join(versionDir, `.inst-${target.rid}`)
    fs.rmSync(out, { recursive: true, force: true })

    console.log(`\n══ Server installer ${target.plat} ══\n`)
    execSync(`bunx electron-builder ${target.eb} --config.directories.output="${out}"`,
        { stdio: "inherit", cwd: installerDir, env })
    // mac ships a .dmg, win a portable .exe, linux an .AppImage (.zip kept as a fallback).
    const built = fs.readdirSync(out).find(f =>
        f.endsWith(".dmg") || f.endsWith(".exe") || f.endsWith(".AppImage") || f.endsWith(".zip"))
    if (!built) throw new Error(`Installer build produced no artifact for ${target.plat}`)
    const outFile = path.join(versionDir, `ARI_Server_Installer_v${installerVersion}_${target.plat}${path.extname(built)}`)
    fs.rmSync(outFile, { force: true })
    fs.renameSync(path.join(out, built), outFile)
    fs.rmSync(out, { recursive: true, force: true })
}

console.log(`\n✓ Server builds → Builds/v${appVersion}/`)
fs.readdirSync(versionDir).filter(f => f.endsWith(".zip")).forEach(f => console.log(`  ${f}`))
