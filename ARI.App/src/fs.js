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

// Regex content search (mirrors the C# SearchFiles tool). The model is told to search with
// regular expressions, so this MUST be a real regex — a literal substring match silently turns
// every alternation/anchor/escape the model writes into a non-match, which is what was forcing it
// to guess instead of locating the exact code. Case-sensitive by default; ignore_case opts in.
function searchFiles(root, pattern, searchPath, glob, ignoreCase) {
    const absRoot   = path.resolve(root)
    const absSearch = path.resolve(root, searchPath ?? ".")
    if (!absSearch.startsWith(absRoot)) throw new Error("Path traversal denied")

    let regex
    try { regex = new RegExp(pattern, ignoreCase ? "i" : "") }
    catch (e) { return [`Invalid regular expression: ${e.message}`] }

    const results = []
    const globExt = (glob && glob !== "*" && glob.startsWith("*.")) ? glob.slice(1) : null
    let truncated = false

    function search(dir) {
        if (truncated) return
        let entries
        try { entries = fs.readdirSync(dir, { withFileTypes: true }) } catch { return }
        for (const entry of entries) {
            if (truncated) return
            if (entry.name.startsWith(".") && entry.name !== ".env") continue
            if (IGNORED_DIRS.has(entry.name)) continue
            const abs = path.join(dir, entry.name)
            if (entry.isDirectory()) { search(abs); continue }
            if (globExt && !entry.name.endsWith(globExt)) continue
            let lines
            try { lines = fs.readFileSync(abs, "utf8").split("\n") } catch { continue }
            for (let i = 0; i < lines.length; i++) {
                if (regex.test(lines[i])) {
                    results.push(`${path.relative(absRoot, abs)}:${i + 1}: ${lines[i].trim()}`)
                    if (results.length >= 200) { truncated = true; break }
                }
            }
        }
    }
    search(absSearch)
    if (truncated) results.push("... (truncated at 200 matches — narrow with path or glob)")
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
        return " None of the file resembles that old_string — don't retype it. Re-read the file, then change the lines you want using start_line/end_line (you can see the line numbers), instead of old_string."

    const to     = Math.min(cLines.length - 1, bestStart + k - 1)
    const region = cLines.slice(bestStart, to + 1).map((l, i) => `${padLine(bestStart + i + 1)}: ${l}`).join("\n")
    return ` The closest matching region is lines ${bestStart + 1}–${to + 1}:\n\`\`\`\n${region}\n\`\`\`\n` +
           `Either copy that text EXACTLY (including indentation) into old_string, or — simpler — edit by line number using start_line/end_line (e.g. start_line ${bestStart + 1}).`
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

// Edit a file by replacing one or more regions. Each region is anchored EITHER by text
// (old_string, the verbatim content) OR by line range (start_line/end_line, 1-based inclusive —
// the numbers shown by read_file). Line anchoring is the reliable path for a model that can see
// numbered lines but can't reproduce a long block verbatim ("delete lines 196-232" just works).
//
// Supports a single edit (top-level old_string/new_string or start_line/end_line) or a batch via
// options.edits. ALL regions are resolved against the ORIGINAL file, checked for overlap, then
// applied highest-offset-first so earlier edits never shift the line numbers / offsets of later
// ones. One read, one write.
function editFile(root, filePath, oldString, newString, options = {}) {
    const abs = path.resolve(root, filePath)
    if (!abs.startsWith(path.resolve(root))) throw new Error("Path traversal denied")
    const content = readFileSyncRetry(abs)

    const rawEdits = Array.isArray(options.edits) && options.edits.length > 0
        ? options.edits
        : [{ old_string: oldString, new_string: newString,
             start_line: options.startLine, end_line: options.endLine, replace_all: options.replaceAll }]

    const nl   = content.includes("\r\n") ? "\r\n" : "\n"
    const norm = s => String(s ?? "").replace(/\r\n/g, "\n").replace(/\r/g, "\n")
    const buf0 = norm(content)

    // Offsets where each line begins, so a 1-based line range maps to a character span.
    const lineStarts = [0]
    for (let i = 0; i < buf0.length; i++) if (buf0[i] === "\n") lineStarts.push(i + 1)
    const totalLines = lineStarts.length

    const spans = []   // { start, len, rep } resolved against buf0
    let anyFuzzy = false, anyPrefixed = false
    const multi = rawEdits.length > 1

    for (let idx = 0; idx < rawEdits.length; idx++) {
        const e = rawEdits[idx]
        const label = multi ? ` (edit ${idx + 1} of ${rawEdits.length})` : ""
        const hasLines = e.start_line != null && e.start_line !== "" && Number.isFinite(Number(e.start_line))

        if (hasLines) {
            const s  = Math.trunc(Number(e.start_line))
            const en = (e.end_line != null && e.end_line !== "" && Number.isFinite(Number(e.end_line))) ? Math.trunc(Number(e.end_line)) : s
            if (s < 1 || s > totalLines || en < s || en > totalLines)
                return { ok: false, error: `start_line/end_line ${s}-${en} is out of range${label} — ${filePath} has ${totalLines} lines. Re-read the file for current line numbers.` }
            const sNew = stripLineNumberPrefix(norm(e.new_string))
            let rep = sNew !== null ? sNew : norm(e.new_string)
            if (sNew !== null) anyPrefixed = true
            const offStart = lineStarts[s - 1]
            let offEnd, hadNL
            if (en < totalLines) { offEnd = lineStarts[en]; hadNL = true }   // include line `en`'s newline
            else { offEnd = buf0.length; hadNL = false }
            if (rep.length > 0 && hadNL && !rep.endsWith("\n")) rep += "\n"   // keep the file line-delimited
            spans.push({ start: offStart, len: offEnd - offStart, rep })
            continue
        }

        // Text-anchored. Strip line-number prefixes the model may have copied from read_file output.
        if (!e.old_string) return { ok: false, error: `Provide old_string or start_line/end_line${label} to edit ${filePath}.` }
        const sOld = stripLineNumberPrefix(norm(e.old_string))
        const sNew = stripLineNumberPrefix(norm(e.new_string))
        const normOld = sOld !== null ? sOld : norm(e.old_string)
        const normNew = sNew !== null ? sNew : norm(e.new_string)
        if (sOld !== null || sNew !== null) anyPrefixed = true

        if (e.replace_all) {
            let from = 0, i, found = false
            while ((i = buf0.indexOf(normOld, from)) !== -1) { spans.push({ start: i, len: normOld.length, rep: normNew }); from = i + normOld.length; found = true }
            if (!found) return { ok: false, error: `old_string not found in ${filePath}${label}. No changes made.${closestRegionHint(buf0, normOld)}` }
            continue
        }

        const m = findMatch(buf0, normOld, filePath, label)
        if (m.error) return { ok: false, error: m.error }
        if (m.fuzzy) anyFuzzy = true
        spans.push({ start: m.start, len: m.len, rep: normNew })
    }

    // Overlap check (all spans are against buf0, so offsets are comparable).
    spans.sort((a, b) => a.start - b.start)
    for (let i = 1; i < spans.length; i++)
        if (spans[i].start < spans[i - 1].start + spans[i - 1].len)
            return { ok: false, error: `Edits overlap in ${filePath} — two edits target the same region. Combine them into one edit.` }

    const firstStart  = spans.length ? spans[0].start : 0
    const firstRepLen = spans.length ? spans[0].rep.length : 0

    // Apply highest-offset-first; lower offsets are then unaffected, so the lowest span's start is
    // still valid in the final buffer (used for the snippet below).
    let buf = buf0
    for (const sp of [...spans].sort((a, b) => b.start - a.start))
        buf = buf.slice(0, sp.start) + sp.rep + buf.slice(sp.start + sp.len)

    writeFileSyncRetry(abs, buf.split("\n").join(nl))

    const uLines = buf.split("\n")
    const tags   = []
    if (anyPrefixed) tags.push("stripped line-number prefixes")
    if (anyFuzzy)    tags.push("matched ignoring indentation/line-ending differences")
    const note   = tags.length ? ` (${tags.join("; ")})` : ""

    if (multi)
        return { ok: true, message: `Successfully edited ${filePath}.${note} Applied ${rawEdits.length} edits (${spans.length} replacements). File is now ${uLines.length} lines.` }

    const editLine = buf.slice(0, firstStart).split("\n").length - 1
    const newCount = firstRepLen === 0 ? 1 : buf.slice(firstStart, firstStart + firstRepLen).split("\n").length
    const from     = Math.max(0, editLine - 5)
    const to       = Math.min(uLines.length - 1, editLine + newCount + 4)
    const snippet  = uLines.slice(from, to + 1).map((l, i) => `${padLine(from + i + 1)}: ${l}`).join("\n")
    const replNote = spans.length > 1 ? ` (${spans.length} occurrences replaced)` : ""
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
