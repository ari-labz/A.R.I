const fs   = require("fs")
const path = require("path")

const IGNORED = new Set([
    "node_modules", ".git", "bin", "obj", "dist", "build",
    ".DS_Store", "__pycache__", ".next", ".nuxt",
])

function readFile(root, filePath) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    return fs.readFileSync(abs, "utf8")
}

function writeFile(root, filePath, content) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    fs.mkdirSync(path.dirname(abs), { recursive: true })
    fs.writeFileSync(abs, content, "utf8")
}

function buildTree(dir, root, results = []) {
    let entries
    try { entries = fs.readdirSync(dir, { withFileTypes: true }) }
    catch { return results }

    for (const entry of entries) {
        if (IGNORED.has(entry.name)) continue
        const abs = path.join(dir, entry.name)
        const rel = path.relative(root, abs)
        if (entry.isDirectory()) {
            buildTree(abs, root, results)
        } else {
            results.push(rel)
        }
    }
    return results
}

function getFileTree(root) {
    return buildTree(path.resolve(root), path.resolve(root))
}

module.exports = { readFile, writeFile, getFileTree }
