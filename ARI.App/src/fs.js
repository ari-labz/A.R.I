const fs   = require("fs")
const path = require("path")
const { exec } = require("child_process")

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

const padLine = n => String(n).padStart(6)

// read_file numbers each line as "  42: code". A weaker model sometimes copies those prefixes
// into old_string. If every non-empty line carries a uniform "<n>: " prefix, strip it so the
// text matches the real file content. Returns null when the prefix isn't uniformly present.
function stripLineNumberPrefix(s) {
    const re       = /^\s*\d+:\s?/
    const lines    = s.split("\n")
    const nonEmpty = lines.filter(l => l.trim().length > 0)
    if (nonEmpty.length === 0 || !nonEmpty.every(l => re.test(l))) return null
    return lines.map(l => l.replace(re, "")).join("\n")
}

// When no match is found, return the file region most similar to old_string, with line numbers,
// so the model can copy the exact bytes instead of guessing again. Uses token-overlap scoring
// (not exact line equality) so it still returns a useful region when old_string was paraphrased
// or reconstructed from memory — the common case when the model's mental model has drifted.
const tokenize = s => (s.toLowerCase().match(/[a-z0-9_]+/g) || [])

function closestRegionHint(content, old) {
    const cLines  = content.split("\n")
    const oLines  = old.split("\n")
    const k       = Math.min(oLines.length, cLines.length)
    if (k === 0 || cLines.length === 0)
        return " Re-read the file to get the exact current text, then retry with a matching old_string."

    const oTokens = oLines.map(tokenize)

    // Slide a window the size of old_string and score it by per-line token overlap against the
    // aligned old line. bestScore starts below zero so the loop always selects a region.
    let bestStart = 0, bestScore = -1
    for (let w = 0; w + k <= cLines.length; w++) {
        let score = 0
        for (let i = 0; i < k; i++) {
            const ct = tokenize(cLines[w + i])
            const ot = oTokens[Math.min(i, oTokens.length - 1)]
            if (ot.length === 0 || ct.length === 0) continue
            const setC = new Set(ct)
            let shared = 0
            for (const t of ot) if (setC.has(t)) shared++
            score += shared / Math.max(ot.length, ct.length)
        }
        if (score > bestScore) { bestScore = score; bestStart = w }
    }

    const to     = Math.min(cLines.length - 1, bestStart + k - 1)
    const region = cLines.slice(bestStart, to + 1).map((l, i) => `${padLine(bestStart + i + 1)}: ${l}`).join("\n")
    return ` The closest matching region is lines ${bestStart + 1}–${to + 1}:\n\`\`\`\n${region}\n\`\`\`\n` +
           `Copy that text exactly (including indentation) into old_string if it is the code you meant to edit.`
}

function editFile(root, filePath, oldString, newString) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    const content = fs.readFileSync(abs, "utf8")
    if (!oldString) return { ok: false, error: `old_string is empty. Provide the exact text to replace in ${filePath}.` }

    // Match in normalized-LF space (tolerates CRLF/CR + indentation drift); re-emit on the
    // file's dominant line ending. A looser tier is only accepted when the match is unique.
    const nl          = content.includes("\r\n") ? "\r\n" : "\n"
    const norm        = s => s.replace(/\r\n/g, "\n").replace(/\r/g, "\n")
    const normContent = norm(content)
    // Strip line-number prefixes the model may have copied from read_file output — independently
    // for old and new, so we never match against (or write back) a stray "  42: " prefix.
    const rawOld      = norm(oldString)
    const rawNew      = norm(newString)
    const strippedOld = stripLineNumberPrefix(rawOld)
    const strippedNew = stripLineNumberPrefix(rawNew)
    const normOld     = strippedOld !== null ? strippedOld : rawOld
    const normNew     = strippedNew !== null ? strippedNew : rawNew
    const prefixed    = strippedOld !== null || strippedNew !== null

    let start = -1, len = 0, fuzzy = false

    // Tier 1 — exact substring.
    {
        let c = 0, i = 0, first = -1
        while ((i = normContent.indexOf(normOld, i)) !== -1) { if (first < 0) first = i; c++; i += normOld.length }
        if (c > 1) return { ok: false, error: `old_string matches ${c} locations in ${filePath}. Add more surrounding context to make it unique.` }
        if (c === 1) { start = first; len = normOld.length }
    }

    // Tier 2 — leading-whitespace-insensitive, contiguous line-block match.
    if (start < 0) {
        const cLines = normContent.split("\n")
        const oLines = normOld.split("\n")
        const oTrim  = oLines.map(l => l.trimStart())
        const k      = oLines.length
        if (k <= cLines.length) {
            let matchStart = -1, matches = 0
            for (let w = 0; w + k <= cLines.length; w++) {
                let all = true
                for (let i = 0; i < k; i++) if (cLines[w + i].trimStart() !== oTrim[i]) { all = false; break }
                if (all) { matches++; if (matchStart < 0) matchStart = w }
            }
            if (matches > 1) return { ok: false, error: `old_string matches ${matches} locations in ${filePath} (ignoring indentation). Add more surrounding context to make it unique.` }
            if (matches === 1) {
                start = 0
                for (let i = 0; i < matchStart; i++) start += cLines[i].length + 1
                len = 0
                for (let i = 0; i < k; i++) len += cLines[matchStart + i].length + (i < k - 1 ? 1 : 0)
                fuzzy = true
            }
        }
    }

    if (start < 0)
        return { ok: false, error: `old_string not found in ${filePath}. No changes made.${closestRegionHint(normContent, normOld)}` }

    const updated = normContent.slice(0, start) + normNew + normContent.slice(start + len)
    fs.writeFileSync(abs, updated.split("\n").join(nl), "utf8")

    // Return a numbered snippet around the edit so the model has current context without re-reading.
    const editLine = normContent.slice(0, start).split("\n").length - 1
    const uLines   = updated.split("\n")
    const newCount = normNew.length === 0 ? 1 : normNew.split("\n").length
    const from     = Math.max(0, editLine - 5)
    const to       = Math.min(uLines.length - 1, editLine + newCount + 4)
    const snippet  = uLines.slice(from, to + 1).map((l, i) => `${padLine(from + i + 1)}: ${l}`).join("\n")
    const tags     = []
    if (prefixed) tags.push("stripped line-number prefixes")
    if (fuzzy)    tags.push("matched ignoring indentation/line-ending differences")
    const note     = tags.length ? ` (${tags.join("; ")})` : ""
    return {
        ok: true,
        message: `Successfully edited ${filePath}.${note} File is now ${uLines.length} lines.\n\n` +
                 `[Updated context — lines ${from + 1}–${to + 1}]\n\`\`\`\n${snippet}\n\`\`\``
    }
}

// Runs a shell command with the project root as cwd. Always resolves (never throws) with the
// exit code and captured output, so the caller can hand failures back to the model as text.
// Authorization (allowlist / user confirmation) is enforced upstream in the renderer.
function runCommand(root, command, timeoutMs = 120000) {
    const cwd = path.resolve(root)
    return new Promise((resolve) => {
        exec(command, { cwd, timeout: timeoutMs, maxBuffer: 4 * 1024 * 1024, windowsHide: true },
            (err, stdout, stderr) => {
                resolve({
                    code:     err && typeof err.code === "number" ? err.code : (err ? 1 : 0),
                    stdout:   stdout ?? "",
                    stderr:   stderr ?? "",
                    timedOut: !!(err && err.killed),
                })
            })
    })
}

module.exports = { readFile, writeFile, getFileTree, listDirectory, searchFiles, editFile, runCommand }
