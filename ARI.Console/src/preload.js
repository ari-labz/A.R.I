const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("ari", {
    state:    ()   => ipcRenderer.invoke("state"),
    start:    ()   => ipcRenderer.invoke("start"),
    stop:     ()   => ipcRenderer.invoke("stop"),
    restart:  ()   => ipcRenderer.invoke("restart"),
    open:     ()   => ipcRenderer.invoke("open"),
    copy:     ()   => ipcRenderer.invoke("copy"),
    logs:     ()   => ipcRenderer.invoke("logs"),
    config:   ()   => ipcRenderer.invoke("config"),
    onLog:    (cb) => ipcRenderer.on("log", (_e, line) => cb(line)),
    onStatus: (cb) => ipcRenderer.on("status", (_e, s) => cb(s)),
})
