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

module.exports = { readFile, writeFile, getFileTree }
