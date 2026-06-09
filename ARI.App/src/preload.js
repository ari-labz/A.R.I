const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("electronBridge", {
    readFile:     (root, path)          => ipcRenderer.invoke("fs:read",        root, path),
    writeFile:    (root, path, content) => ipcRenderer.invoke("fs:write",       root, path, content),
    pickFolder:   ()                    => ipcRenderer.invoke("fs:pick-folder"),
    getFileTree:  (root)                => ipcRenderer.invoke("fs:tree",        root),
    getEndpoint:  ()                    => ipcRenderer.invoke("cfg:get-endpoint"),
    setEndpoint:  (url)                 => ipcRenderer.invoke("cfg:set-endpoint", url),
    moveWindowBy: (dx, dy)              => ipcRenderer.invoke("window:move-by", dx, dy),
    markReady:    ()                    => ipcRenderer.invoke("app:ready"),
})
