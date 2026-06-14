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

// Transient FS errors (EACCES/EPERM/EBUSY) can hit a file we just touched — e.g. an antivirus
// scanner, indexer, or a back-to-back read+write race holding a brief lock. These clear in
// milliseconds, so retry a few times before giving up rather than surfacing a scary "Permission
// denied" to the model (which previously made it abandon edit_file and fall back to sed).
function sleepSync(ms) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms)
}
const TRANSIENT = new Set(["EACCES", "EPERM", "EBUSY", "ETXTBSY"])
function readFileSyncRetry(abs) {
    for (let attempt = 0; ; attempt++) {
        try { return fs.readFileSync(abs, "utf8") }
        catch (e) { if (attempt < 3 && TRANSIENT.has(e.code)) { sleepSync(40 * (attempt + 1)); continue } throw e }
    }
}
function writeFileSyncRetry(abs, data) {
    for (let attempt = 0; ; attempt++) {
        try { return fs.writeFileSync(abs, data, "utf8") }
        catch (e) { if (attempt < 3 && TRANSIENT.has(e.code)) { sleepSync(40 * (attempt + 1)); continue } throw e }
    }
}

function readFile(root, filePath) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    return readFileSyncRetry(abs)
}

function writeFile(root, filePath, content) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    fs.mkdirSync(path.dirname(abs), { recursive: true })
    writeFileSyncRetry(abs, content)
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

    // No region shares any tokens with old_string — pointing at line 1 would be misleading. Tell
    // the model the text isn't there so it re-reads / searches instead of retrying blindly.
    if (bestScore <= 0)
        return " None of the file resembles that old_string — it may have already been changed or never existed. Re-read the file (or search_files for a nearby anchor) before retrying."

    const to     = Math.min(cLines.length - 1, bestStart + k - 1)
    const region = cLines.slice(bestStart, to + 1).map((l, i) => `${padLine(bestStart + i + 1)}: ${l}`).join("\n")
    return ` The closest matching region is lines ${bestStart + 1}–${to + 1}:\n\`\`\`\n${region}\n\`\`\`\n` +
           `Copy that text exactly (including indentation) into old_string if it is the code you meant to edit.`
}

// Locate `normOld` in normalized-LF `buf`. Tier 1: exact unique substring. Tier 2: leading-
// whitespace-insensitive contiguous line block. Returns {start, len, fuzzy} or {error}.
function findMatch(buf, normOld, filePath, label) {
    // Tier 1 — exact substring (must be unique).
    {
        let c = 0, i = 0, first = -1
        while ((i = buf.indexOf(normOld, i)) !== -1) { if (first < 0) first = i; c++; i += normOld.length }
        if (c > 1) return { error: `old_string matches ${c} locations in ${filePath}${label}. Add more surrounding context to make it unique, or set replace_all to change them all.` }
        if (c === 1) return { start: first, len: normOld.length, fuzzy: false }
    }
    // Tier 2 — leading-whitespace-insensitive, contiguous line-block match.
    const cLines = buf.split("\n")
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
        if (matches > 1) return { error: `old_string matches ${matches} locations in ${filePath}${label} (ignoring indentation). Add more surrounding context to make it unique.` }
        if (matches === 1) {
            let start = 0
            for (let i = 0; i < matchStart; i++) start += cLines[i].length + 1
            let len = 0
            for (let i = 0; i < k; i++) len += cLines[matchStart + i].length + (i < k - 1 ? 1 : 0)
            return { start, len, fuzzy: true }
        }
    }
    return { error: `old_string not found in ${filePath}${label}. No changes made.${closestRegionHint(buf, normOld)}` }
}

// Targeted find-and-replace. Supports a single old/new pair OR a MultiEdit-style batch via
// options.edits (each {old_string, new_string, replace_all}). Batched edits are applied
// sequentially against ONE in-memory buffer, so the line shifts from earlier edits never
// invalidate later ones — the whole set lands in a single read + single write.
function editFile(root, filePath, oldString, newString, options = {}) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    const content = readFileSyncRetry(abs)

    const edits = Array.isArray(options.edits) && options.edits.length > 0
        ? options.edits.map(e => ({ old: e.old_string, new: e.new_string ?? "", all: !!e.replace_all }))
        : [{ old: oldString, new: newString ?? "", all: !!options.replaceAll }]

    const nl   = content.includes("\r\n") ? "\r\n" : "\n"
    const norm = s => String(s).replace(/\r\n/g, "\n").replace(/\r/g, "\n")
    let buf = norm(content)

    let anyFuzzy = false, anyPrefixed = false, replacements = 0, firstStart = -1, firstNewLen = 0
    for (let idx = 0; idx < edits.length; idx++) {
        const ed    = edits[idx]
        const label = edits.length > 1 ? ` (edit ${idx + 1} of ${edits.length})` : ""
        if (!ed.old) return { ok: false, error: `old_string is empty${label}. Provide the exact text to replace in ${filePath}.` }

        // Strip line-number prefixes the model may have copied from read_file output, independently
        // for old and new, so we never match against (or write back) a stray "  42: " prefix.
        const rawOld = norm(ed.old)
        const rawNew = norm(ed.new)
        const sOld   = stripLineNumberPrefix(rawOld)
        const sNew   = stripLineNumberPrefix(rawNew)
        const normOld = sOld !== null ? sOld : rawOld
        const normNew = sNew !== null ? sNew : rawNew
        if (sOld !== null || sNew !== null) anyPrefixed = true

        if (ed.all) {
            if (!buf.includes(normOld))
                return { ok: false, error: `old_string not found in ${filePath}${label}. No changes made.${closestRegionHint(buf, normOld)}` }
            const at    = buf.indexOf(normOld)
            const count = buf.split(normOld).length - 1
            buf = buf.split(normOld).join(normNew)
            replacements += count
            if (firstStart < 0) { firstStart = at; firstNewLen = normNew.length }
            continue
        }

        const m = findMatch(buf, normOld, filePath, label)
        if (m.error) return { ok: false, error: m.error }
        buf = buf.slice(0, m.start) + normNew + buf.slice(m.start + m.len)
        if (m.fuzzy) anyFuzzy = true
        replacements++
        if (firstStart < 0) { firstStart = m.start; firstNewLen = normNew.length }
    }

    writeFileSyncRetry(abs, buf.split("\n").join(nl))

    const uLines = buf.split("\n")
    const tags   = []
    if (anyPrefixed) tags.push("stripped line-number prefixes")
    if (anyFuzzy)    tags.push("matched ignoring indentation/line-ending differences")
    const note   = tags.length ? ` (${tags.join("; ")})` : ""

    // Multi-edit: report a summary (per-edit snippets would be misaligned after splicing).
    if (edits.length > 1)
        return {
            ok: true,
            message: `Successfully edited ${filePath}.${note} Applied ${edits.length} edits (${replacements} replacements). File is now ${uLines.length} lines.`
        }

    // Single edit: return a numbered snippet around the change so the model has current context.
    const editLine = buf.slice(0, firstStart).split("\n").length - 1
    const newCount = firstNewLen === 0 ? 1 : buf.slice(firstStart, firstStart + firstNewLen).split("\n").length
    const from     = Math.max(0, editLine - 5)
    const to       = Math.min(uLines.length - 1, editLine + newCount + 4)
    const snippet  = uLines.slice(from, to + 1).map((l, i) => `${padLine(from + i + 1)}: ${l}`).join("\n")
    const replNote = replacements > 1 ? ` (${replacements} occurrences replaced)` : ""
    return {
        ok: true,
        message: `Successfully edited ${filePath}.${note}${replNote} File is now ${uLines.length} lines.\n\n` +
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

function globToRegex(glob) {
    let re = "^"
    for (let i = 0; i < glob.length; i++) {
        const c = glob[i]
        if (c === "*") { if (glob[i + 1] === "*") { re += ".*"; i++ } else re += "[^/]*" }
        else if (c === "?") re += "[^/]"
        else if (".()+|^$\\{}[]".includes(c)) re += "\\" + c
        else re += c
    }
    return new RegExp(re + "$", "i")
}

// Find files by name/glob. Reuses buildTree (source-file filter + ignore list); matches the glob
// against both the relative path and the bare filename. Capped at 200 results.
function findFiles(root, pattern, searchPath) {
    const absRoot = path.resolve(root)
    const base    = searchPath ? path.resolve(root, searchPath) : absRoot
    if (!base.startsWith(absRoot)) throw new Error("Path traversal denied")
    const rx = globToRegex(pattern)
    const results = []
    for (const rel of buildTree(base, absRoot)) {
        const name = rel.split("/").pop()
        if (rx.test(rel) || rx.test(name)) {
            results.push(rel)
            if (results.length >= 200) break
        }
    }
    return results.sort()
}

function deleteFile(root, filePath) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    if (!fs.existsSync(abs)) return { ok: false, error: `File not found: ${filePath}` }
    fs.rmSync(abs, { force: false })
    return { ok: true }
}

function moveFile(root, source, destination) {
    const absRoot = path.resolve(root)
    const absSrc  = path.resolve(root, source)
    const absDst  = path.resolve(root, destination)
    if (!absSrc.startsWith(absRoot) || !absDst.startsWith(absRoot)) throw new Error("Path traversal denied")
    if (!fs.existsSync(absSrc)) return { ok: false, error: `Source not found: ${source}` }
    if (fs.existsSync(absDst))  return { ok: false, error: `Destination already exists: ${destination}` }
    fs.mkdirSync(path.dirname(absDst), { recursive: true })
    fs.renameSync(absSrc, absDst)
    return { ok: true }
}

module.exports = { readFile, writeFile, getFileTree, listDirectory, searchFiles, editFile, runCommand, findFiles, deleteFile, moveFile }
