const fs   = require("fs")
const path = require("path")

let _logPath = null
let _stream  = null

function init(userDataDir) {
    _logPath = path.join(userDataDir, "ARIClient.log")
    // Truncate on each launch (mirrors ARI.log behaviour)
    _stream = fs.createWriteStream(_logPath, { flags: "w" })
    _stream.on("error", () => { /* nowhere to write — give up silently */ })
}

function _ts() {
    const d = new Date()
    const p = n => String(n).padStart(2, "0")
    return `[${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}]`
}

function _write(context, level, msg) {
    const line = `${_ts()} [${context}] ${level === "ERR" ? "ERROR " : ""}${msg}\n`
    if (_stream) _stream.write(line)
    // Also mirror to console so DevTools / attached debugger can see it
    if (level === "ERR") console.error(line.trimEnd())
    else                 console.log(line.trimEnd())
}

function makeLogger(context) {
    return {
        info:  msg => _write(context, "INF", msg),
        warn:  msg => _write(context, "WRN", `WARNING ${msg}`),
        error: (msg, err) => {
            const detail = err
                ? `${msg}\n  ${err?.stack ?? err}`
                : msg
            _write(context, "ERR", detail)
        },
    }
}

function getLogPath() { return _logPath }

module.exports = { init, makeLogger, getLogPath }
