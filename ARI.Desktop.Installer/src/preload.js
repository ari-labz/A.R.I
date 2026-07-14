const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("launcher", {
    getToken:           ()           => ipcRenderer.invoke("get-token"),
    saveToken:          (t)          => ipcRenderer.invoke("save-token", t),
    fetchRelease:       (t)          => ipcRenderer.invoke("fetch-release", t),
    versionInstalled:   (tag)        => ipcRenderer.invoke("version-installed", tag),
    downloadAndInstall: (t, release) => ipcRenderer.invoke("download-and-install", t, release),
    launchAri:          (dir)        => ipcRenderer.invoke("launch-ari", dir),
    onProgress:         (cb)         => ipcRenderer.on("download-progress", (_, d) => cb(d)),
    onStatus:           (cb)         => ipcRenderer.on("status", (_, s) => cb(s)),
})
