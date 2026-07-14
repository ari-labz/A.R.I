// Builds the A·R·I server into per-platform release zips that the installer downloads.
// Each target is a self-contained `dotnet publish` (bundles the .NET runtime, so users need
// nothing preinstalled), zipped as ARI-{version}-{platform}.zip. The csproj already copies
// wwwroot, External/StyleTTS2, the Listener scripts, and manifest.json into the output, so
// they ride along. Python venvs are NOT bundled — the server provisions them on first run.
//
// Usage:  node build-release.js
// Output: Builds/v{version}/ARI-{version}-{mac,win,linux}.zip

const { execSync } = require("child_process")
const fs   = require("fs")
const path = require("path")

const version    = JSON.parse(fs.readFileSync(path.join(__dirname, "manifest.json"), "utf8")).version
const buildsDir  = path.join(__dirname, "Builds")
const versionDir = path.join(buildsDir, `v${version}`)
const csproj     = path.join(__dirname, "ARI.Core", "ARI.Core.csproj")

fs.mkdirSync(versionDir, { recursive: true })

// Keep bun on PATH — the csproj's BuildUI target shells out to it to build the React UI.
const env = { ...process.env, PATH: `${path.join(process.env.HOME || "", ".bun", "bin")}:${process.env.PATH}` }

const targets = [
    { rid: "osx-arm64", zip: `ARI-${version}-mac.zip`   },
    { rid: "win-x64",   zip: `ARI-${version}-win.zip`   },
    { rid: "linux-x64", zip: `ARI-${version}-linux.zip` },
]

for (const { rid, zip } of targets) {
    const stage    = path.join(versionDir, `.stage-${rid}`)
    const buildDir = path.join(stage, "build")     // throwaway build output (keeps it off /Applications)
    const pubDir   = path.join(stage, "publish")   // what we zip
    const outZip   = path.join(versionDir, zip)

    fs.rmSync(stage, { recursive: true, force: true })
    fs.mkdirSync(pubDir, { recursive: true })
    fs.rmSync(outZip, { force: true })

    console.log(`\n── Publishing server (${rid}) → ${zip}\n`)
    execSync(
        `dotnet publish "${csproj}" -c Release -r ${rid} --self-contained true ` +
        `-p:AppInstallRoot="${buildDir}" -o "${pubDir}"`,
        { stdio: "inherit", env },
    )

    console.log(`\n── Zipping ${zip}\n`)
    // Zip the publish contents at the archive root, so extraction yields ARI.Core at the top.
    execSync(`zip -r -q "${outZip}" .`, { stdio: "inherit", cwd: pubDir })

    fs.rmSync(stage, { recursive: true, force: true })
}

console.log(`\n✓ Server builds → Builds/v${version}/`)
fs.readdirSync(versionDir).forEach(f => console.log(`  ${f}`))
