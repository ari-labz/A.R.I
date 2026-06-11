const { execSync } = require("child_process")
const fs           = require("fs")
const path         = require("path")
const { version }  = require("./package.json")

const repoRoot   = path.join(__dirname, "..")
const buildsDir  = path.join(repoRoot, "Builds")
const versionDir = path.join(buildsDir, `v${version}`)
const tmpDir     = path.join(versionDir, ".tmp")
const eb         = path.join(__dirname, "node_modules", ".bin", "electron-builder")

fs.mkdirSync(versionDir, { recursive: true })
fs.mkdirSync(tmpDir,     { recursive: true })

// ── Bump RequiredClientVersion in InfoController ──────────────────────────────

const infoController = path.join(repoRoot, "ARI.API", "Controllers", "InfoController.cs")
const infoSrc = fs.readFileSync(infoController, "utf8")
const updatedInfo = infoSrc.replace(
    /private const string RequiredClientVersion = "[^"]+";/,
    `private const string RequiredClientVersion = "${version}";`
)
if (updatedInfo !== infoSrc) {
    fs.writeFileSync(infoController, updatedInfo, "utf8")
    console.log(`\n── Bumped RequiredClientVersion to ${version}\n`)
}

// ── Electron builds ───────────────────────────────────────────────────────────

const platforms = [
    { flag: "--win   --x64", zip: `ARI-${version}-win.zip`   },
    { flag: "--linux --x64", zip: `ARI-${version}-linux.zip` },
    { flag: "--mac",         zip: `ARI-${version}-mac.zip`   },
]

for (const { flag, zip } of platforms) {
    console.log(`\n── Building ARI ${zip}\n`)
    execSync(`"${eb}" ${flag} --config.directories.output="${tmpDir}"`, {
        stdio: "inherit",
        cwd:   __dirname,
    })
    // Move just the zip to versionDir, discard everything else
    const built = fs.readdirSync(tmpDir).find(f => f.endsWith(".zip"))
    if (!built) throw new Error(`No zip found after building ${zip}`)
    fs.renameSync(path.join(tmpDir, built), path.join(versionDir, zip))
    fs.rmSync(tmpDir, { recursive: true, force: true })
    fs.mkdirSync(tmpDir, { recursive: true })
}

// ── Launcher builds ───────────────────────────────────────────────────────────

const launcherDir = path.join(repoRoot, "ARI.Launcher")
const launcherEb  = path.join(launcherDir, "node_modules", ".bin", "electron-builder")

// Ensure launcher deps are installed
if (!fs.existsSync(launcherEb)) {
    console.log("\n── Installing launcher dependencies\n")
    execSync(`bun install`, { stdio: "inherit", cwd: launcherDir })
}

const launcherPlatforms = [
    { flag: "--win   --x64", zip: "ARILauncher-win.zip"   },
    { flag: "--linux --x64", zip: "ARILauncher-linux.zip" },
    { flag: "--mac",         zip: "ARILauncher-mac.zip"   },
]

for (const { flag, zip } of launcherPlatforms) {
    console.log(`\n── Building Launcher ${zip}\n`)
    execSync(`"${launcherEb}" ${flag} --config.directories.output="${tmpDir}"`, {
        stdio: "inherit",
        cwd:   launcherDir,
    })
    const built = fs.readdirSync(tmpDir).find(f => f.endsWith(".zip"))
    if (!built) throw new Error(`No zip found after building launcher ${zip}`)
    fs.renameSync(path.join(tmpDir, built), path.join(versionDir, zip))
    fs.rmSync(tmpDir, { recursive: true, force: true })
    fs.mkdirSync(tmpDir, { recursive: true })
}

// ── Cleanup ───────────────────────────────────────────────────────────────────

fs.rmSync(tmpDir, { recursive: true, force: true })

console.log(`\n✓ All builds complete → Builds/v${version}/`)
fs.readdirSync(versionDir).forEach(f => console.log(`  ${f}`))
