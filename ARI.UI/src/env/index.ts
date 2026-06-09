export interface AriEnvironment {
  isDesktop: boolean
  readFile(root: string, path: string): Promise<string>
  writeFile(root: string, path: string, content: string): Promise<void>
  pickFolder(): Promise<string | null>
  getFileTree(root: string): Promise<string[]>
  getEndpoint(): string
  setEndpoint(url: string): void
}

declare global {
  interface Window {
    electronBridge?: {
      readFile(root: string, path: string): Promise<string>
      writeFile(root: string, path: string, content: string): Promise<void>
      pickFolder(): Promise<string | null>
      getFileTree(root: string): Promise<string[]>
      getEndpoint(): string
      setEndpoint(url: string): void
      moveWindowBy(dx: number, dy: number): void
      markReady(): void
    }
  }
}

const browserEnv: AriEnvironment = {
  isDesktop: false,
  readFile:    () => Promise.reject(new Error("No local filesystem in browser")),
  writeFile:   () => Promise.reject(new Error("No local filesystem in browser")),
  pickFolder:  () => Promise.resolve(null),
  getFileTree: () => Promise.resolve([]),
  getEndpoint: () => "",
  setEndpoint: () => {},
}

const electronEnv: AriEnvironment = {
  isDesktop: true,
  readFile:    (root, path) => window.electronBridge!.readFile(root, path),
  writeFile:   (root, path, content) => window.electronBridge!.writeFile(root, path, content),
  pickFolder:  () => window.electronBridge!.pickFolder(),
  getFileTree: (root) => window.electronBridge!.getFileTree(root),
  getEndpoint: () => window.electronBridge!.getEndpoint(),
  setEndpoint: (url) => window.electronBridge!.setEndpoint(url),
}

export const env: AriEnvironment = window.electronBridge ? electronEnv : browserEnv
