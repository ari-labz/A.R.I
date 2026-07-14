const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("installer", {
    getPlatform:        ()               => ipcRenderer.invoke("get-platform"),
    needsToken:         ()               => ipcRenderer.invoke("needs-token"),
    getToken:           ()               => ipcRenderer.invoke("get-token"),
    saveToken:          (t)              => ipcRenderer.invoke("save-token", t),
    fetchReleases:      (t)              => ipcRenderer.invoke("fetch-releases", t),
    installedInfo:      ()               => ipcRenderer.invoke("installed-info"),
    downloadAndInstall: (t, rel, opts)   => ipcRenderer.invoke("download-and-install", t, rel, opts),
    launchServer:       (v)              => ipcRenderer.invoke("launch-server", v),
    onProgress:         (cb)             => ipcRenderer.on("download-progress", (_, d) => cb(d)),
    onStatus:           (cb)             => ipcRenderer.on("status", (_, s) => cb(s)),
})
