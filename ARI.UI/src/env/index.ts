// A single line-range edit, or a MultiEdit-style batch applied against one buffer in order.
export interface EditOptions {
  startLine?: number
  endLine?: number
  edits?: { new_string?: string; start_line?: number; end_line?: number }[]
}

export interface AriEnvironment {
  isDesktop: boolean
  readFile(root: string, path: string): Promise<string>
  writeFile(root: string, path: string, content: string): Promise<void>
  listDirectory(root: string, dirPath?: string, depth?: number): Promise<string[]>
  searchFiles(root: string, pattern: string, searchPath?: string, glob?: string, ignoreCase?: boolean): Promise<string[]>
  editFile(root: string, filePath: string, newString: string, options?: EditOptions): Promise<{ ok: boolean; error?: string; message?: string }>
  runCommand(root: string, command: string): Promise<{ code: number; stdout: string; stderr: string; timedOut: boolean }>
  getCommandAllowlist(): Promise<string[]>
  setCommandAllowlist(list: string[]): Promise<void>
  findFiles(root: string, pattern: string, searchPath?: string): Promise<string[]>
  deleteFile(root: string, filePath: string): Promise<{ ok: boolean; error?: string }>
  moveFile(root: string, source: string, destination: string): Promise<{ ok: boolean; error?: string }>
  pickFolder(): Promise<string | null>
  getFileTree(root: string): Promise<string[]>
  getShallowTree(root: string, depth?: number): Promise<string[]>
  getEndpoint(): string
  setEndpoint(url: string): void
  getLocalPath(projectId: string): Promise<string | null>
  setLocalPath(projectId: string, path: string | null): Promise<void>
  getVersion(): Promise<string | null>
}

declare global {
  interface Window {
    electronBridge?: {
      platform: string
      readFile(root: string, path: string): Promise<string>
      writeFile(root: string, path: string, content: string): Promise<void>
      listDirectory(root: string, dirPath?: string, depth?: number): Promise<string[]>
      searchFiles(root: string, pattern: string, searchPath?: string, glob?: string, ignoreCase?: boolean): Promise<string[]>
      editFile(root: string, filePath: string, newString: string, options?: EditOptions): Promise<{ ok: boolean; error?: string; message?: string }>
      runCommand(root: string, command: string): Promise<{ code: number; stdout: string; stderr: string; timedOut: boolean }>
      getCommandAllowlist(): Promise<string[]>
      setCommandAllowlist(list: string[]): Promise<void>
      findFiles(root: string, pattern: string, searchPath?: string): Promise<string[]>
      deleteFile(root: string, filePath: string): Promise<{ ok: boolean; error?: string }>
      moveFile(root: string, source: string, destination: string): Promise<{ ok: boolean; error?: string }>
      pickFolder(): Promise<string | null>
      getFileTree(root: string): Promise<string[]>
      getShallowTree(root: string, depth?: number): Promise<string[]>
      getEndpoint(): string
      setEndpoint(url: string): void
      moveWindowBy(dx: number, dy: number): void
      closeWindow(): void
      minimizeWindow(): void
      maximizeWindow(): void
      markReady(): void
      getLocalPath(projectId: string): Promise<string | null>
      setLocalPath(projectId: string, path: string | null): Promise<void>
      getVersion(): Promise<string>
    }
  }
}

const browserEnv: AriEnvironment = {
  isDesktop:     false,
  readFile:      () => Promise.reject(new Error("No local filesystem in browser")),
  writeFile:     () => Promise.reject(new Error("No local filesystem in browser")),
  listDirectory:  () => Promise.reject(new Error("No local filesystem in browser")),
  getShallowTree: () => Promise.resolve([]),
  searchFiles:   () => Promise.reject(new Error("No local filesystem in browser")),
  editFile:      () => Promise.reject(new Error("No local filesystem in browser")),
  runCommand:          () => Promise.reject(new Error("No local shell in browser")),
  getCommandAllowlist: () => Promise.resolve([]),
  setCommandAllowlist: () => Promise.resolve(),
  findFiles:            () => Promise.reject(new Error("No local filesystem in browser")),
  deleteFile:           () => Promise.reject(new Error("No local filesystem in browser")),
  moveFile:             () => Promise.reject(new Error("No local filesystem in browser")),
  pickFolder:    () => Promise.resolve(null),
  getFileTree:   () => Promise.resolve([]),
  getEndpoint:   () => "",
  setEndpoint:   () => {},
  getLocalPath:  () => Promise.resolve(null),
  setLocalPath:  () => Promise.resolve(),
  getVersion:    () => Promise.resolve(null),
}

const electronEnv: AriEnvironment = {
  isDesktop:     true,
  readFile:      (root, path)                        => window.electronBridge!.readFile(root, path),
  writeFile:     (root, path, content)               => window.electronBridge!.writeFile(root, path, content),
  listDirectory:  (root, dirPath, depth)              => window.electronBridge!.listDirectory(root, dirPath, depth),
  searchFiles:   (root, pattern, searchPath, glob, ignoreCase) => window.electronBridge!.searchFiles(root, pattern, searchPath, glob, ignoreCase),
  editFile:      (root, filePath, newStr, options) => window.electronBridge!.editFile(root, filePath, newStr, options),
  runCommand:          (root, command)               => window.electronBridge!.runCommand(root, command),
  getCommandAllowlist: ()                            => window.electronBridge!.getCommandAllowlist(),
  setCommandAllowlist: (list)                        => window.electronBridge!.setCommandAllowlist(list),
  findFiles:            (root, pattern, searchPath)  => window.electronBridge!.findFiles(root, pattern, searchPath),
  deleteFile:           (root, filePath)             => window.electronBridge!.deleteFile(root, filePath),
  moveFile:             (root, source, destination)  => window.electronBridge!.moveFile(root, source, destination),
  pickFolder:    ()                                  => window.electronBridge!.pickFolder(),
  getFileTree:    (root)                              => window.electronBridge!.getFileTree(root),
  getShallowTree: (root, depth)                      => window.electronBridge!.getShallowTree(root, depth),
  getEndpoint:   ()                                  => window.electronBridge!.getEndpoint(),
  setEndpoint:   (url)                               => window.electronBridge!.setEndpoint(url),
  getLocalPath:  (projectId)                         => window.electronBridge!.getLocalPath(projectId),
  setLocalPath:  (projectId, path)                   => window.electronBridge!.setLocalPath(projectId, path),
  getVersion:    ()                                  => window.electronBridge!.getVersion(),
}

export const env: AriEnvironment = window.electronBridge ? electronEnv : browserEnv
