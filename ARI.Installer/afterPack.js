// Ad-hoc code-signs the packaged macOS .app so Gatekeeper reports "unidentified developer"
// (right-click -> Open works) instead of "damaged" (no GUI bypass). Free: no Apple certificate.
const { execFileSync } = require("child_process")
const path = require("path")

exports.default = async function afterPack(context) {
    if (context.electronPlatformName !== "darwin") return
    const appPath = path.join(context.appOutDir, `${context.packager.appInfo.productFilename}.app`)
    execFileSync("codesign", ["--deep", "--force", "--sign", "-", appPath], { stdio: "inherit" })
    console.log(`  * ad-hoc signed ${appPath}`)
}
