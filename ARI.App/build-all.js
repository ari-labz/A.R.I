const { execSync } = require("child_process")
const path         = require("path")
const { version }  = require("./package.json")

const buildsDir = path.join(__dirname, "..", "Builds")
const versionDir = path.join(buildsDir, `v${version}`)
const eb = path.join(__dirname, "node_modules", ".bin", "electron-builder")

const platforms = [
    { flag: "--win   --x64", dir: "Windows" },
    { flag: "--linux --x64", dir: "Linux"   },
    { flag: "--mac",         dir: "macOS"   },
]

for (const { flag, dir } of platforms) {
    const out = path.join(versionDir, dir)
    console.log(`\n── Building ${dir} → ${out}\n`)
    execSync(`"${eb}" ${flag} --config.directories.output="${out}"`, {
        stdio: "inherit",
        cwd:   __dirname,
    })
}

console.log(`\n✓ All builds complete → Builds/v${version}/`)
