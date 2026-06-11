const fs   = require("fs")
const path = require("path")

const IGNORED_DIRS = new Set([
    "node_modules", ".git", "bin", "obj", "dist", "build",
    ".DS_Store", "__pycache__", ".next", ".nuxt",
    ".idea", ".vs", ".vscode", "coverage", "out", "target",
    "vendor", "packages", ".gradle", "Pods",
    // Large asset/model directories that are never source code
    "Models", "Voices", "External",
])

// Only files with these extensions are included in the tree sent to ARI.
// Everything else (binaries, models, images, etc.) is omitted.
const SOURCE_EXTS = new Set([
    ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
    ".json", ".jsonc",
    ".md", ".txt", ".rst",
    ".yaml", ".yml", ".toml", ".ini", ".conf", ".env",
    ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1",
    ".css", ".scss", ".less", ".sass",
    ".html", ".htm", ".razor", ".cshtml", ".svelte", ".vue",
    ".xml", ".csproj", ".sln", ".props", ".targets",
    ".py", ".pyi", ".go", ".rs", ".cpp", ".cc", ".cxx",
    ".c", ".h", ".hpp", ".java", ".kt", ".kts", ".swift",
    ".rb", ".php", ".lua", ".ex", ".exs", ".hs", ".ml",
    ".sql", ".graphql", ".gql", ".proto",
    ".dockerfile", ".dockerignore", ".gitignore", ".gitattributes",
    ".editorconfig", ".eslintrc", ".prettierrc",
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
        // Skip hidden files/dirs and ignored directories
        if (entry.name.startsWith(".") && entry.name !== ".env") continue
        if (IGNORED_DIRS.has(entry.name)) continue

        const abs = path.join(dir, entry.name)
        const rel = path.relative(root, abs)

        if (entry.isDirectory()) {
            buildTree(abs, root, results)
        } else {
            const ext = path.extname(entry.name).toLowerCase()
            if (SOURCE_EXTS.has(ext) || SOURCE_EXTS.has(entry.name.toLowerCase())) {
                results.push(rel)
            }
        }
    }
    return results
}

function getFileTree(root) {
    return buildTree(path.resolve(root), path.resolve(root))
}

function listDirectory(root, dirPath) {
    const abs = path.resolve(root, dirPath ?? ".")
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    let entries
    try { entries = fs.readdirSync(abs, { withFileTypes: true }) }
    catch (e) { throw new Error(`Directory not found: ${dirPath}`) }
    return entries
        .filter(e => !e.name.startsWith(".") || e.name === ".env")
        .map(e => e.name + (e.isDirectory() ? "/" : ""))
        .sort()
}

function searchFiles(root, pattern, searchPath, glob) {
    const absRoot   = path.resolve(root)
    const absSearch = path.resolve(root, searchPath ?? ".")
    if (!absSearch.startsWith(absRoot)) throw new Error("Path traversal denied")

    const results = []
    const globExt = (glob && glob !== "*" && glob.startsWith("*.")) ? glob.slice(1) : null

    function search(dir) {
        if (results.length >= 200) return
        let entries
        try { entries = fs.readdirSync(dir, { withFileTypes: true }) } catch { return }
        for (const entry of entries) {
            if (results.length >= 200) return
            if (entry.name.startsWith(".") && entry.name !== ".env") continue
            if (IGNORED_DIRS.has(entry.name)) continue
            const abs = path.join(dir, entry.name)
            if (entry.isDirectory()) {
                search(abs)
            } else {
                if (globExt && !entry.name.endsWith(globExt)) continue
                try {
                    const lines = fs.readFileSync(abs, "utf8").split("\n")
                    for (let i = 0; i < lines.length && results.length < 200; i++) {
                        if (lines[i].toLowerCase().includes(pattern.toLowerCase())) {
                            results.push(`${path.relative(absRoot, abs)}:${i + 1}: ${lines[i].trim()}`)
                        }
                    }
                } catch { /* skip unreadable */ }
            }
        }
    }
    search(absSearch)
    return results
}

function editFile(root, filePath, oldString, newString) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    const content = fs.readFileSync(abs, "utf8")

    // Exact match
    let count = 0, idx = 0
    while ((idx = content.indexOf(oldString, idx)) !== -1) { count++; idx += oldString.length }
    if (count === 1) {
        fs.writeFileSync(abs, content.replace(oldString, newString), "utf8")
        return { ok: true }
    }
    if (count > 1) return { ok: false, error: `old_string matches ${count} locations in ${filePath}. Add more surrounding context to make it unique.` }

    // Fallback: normalize CRLF → LF on both sides (LLM often drops \r when generating old_string)
    const normContent = content.replace(/\r\n/g, "\n")
    const normOld     = oldString.replace(/\r\n/g, "\n")
    const normNew     = newString.replace(/\r\n/g, "\n")
    let normCount = 0, normIdx = 0
    while ((normIdx = normContent.indexOf(normOld, normIdx)) !== -1) { normCount++; normIdx += normOld.length }
    if (normCount === 0) return { ok: false, error: `old_string not found in ${filePath}. No changes made.` }
    if (normCount > 1)  return { ok: false, error: `old_string matches ${normCount} locations in ${filePath}. Add more surrounding context to make it unique.` }
    fs.writeFileSync(abs, normContent.replace(normOld, normNew), "utf8")
    return { ok: true }
}

module.exports = { readFile, writeFile, getFileTree, listDirectory, searchFiles, editFile }
