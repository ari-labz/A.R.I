const { contextBridge, ipcRenderer } = require("electron")

contextBridge.exposeInMainWorld("electronBridge", {
    platform:     process.platform,
    readFile:     (root, path)          => ipcRenderer.invoke("fs:read",           root, path),
    writeFile:    (root, path, content) => ipcRenderer.invoke("fs:write",          root, path, content),
    pickFolder:   ()                    => ipcRenderer.invoke("fs:pick-folder"),
    getFileTree:  (root)                => ipcRenderer.invoke("fs:tree",           root),
    getEndpoint:  ()                    => ipcRenderer.invoke("cfg:get-endpoint"),
    setEndpoint:  (url)                 => ipcRenderer.invoke("cfg:set-endpoint",  url),
    getLocalPath: (projectId)           => ipcRenderer.invoke("project:get-path",  projectId),
    setLocalPath: (projectId, path)     => ipcRenderer.invoke("project:set-path",  projectId, path),
    moveWindowBy: (dx, dy)              => ipcRenderer.invoke("window:move-by",    dx, dy),
    closeWindow:  ()                    => ipcRenderer.invoke("window:close"),
    markReady:    ()                    => ipcRenderer.invoke("app:ready"),
})
