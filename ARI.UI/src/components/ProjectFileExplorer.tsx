import { useState, useEffect, useCallback, useRef } from "react"
import { apiFetch, tokenUrl } from "../auth"

interface FileEntry {
    name:       string
    isDir:      boolean
    size:       number | null
    modifiedAt: string
}

interface Props {
    projectId: string
}

function formatSize(bytes: number | null): string {
    if (bytes == null) return ""
    if (bytes < 1024) return `${bytes} B`
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
    return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}

function FileIcon({ isDir, name }: { isDir: boolean; name: string }) {
    if (isDir) {
        return (
            <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#7abacc" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" fill="#e8f2f5" />
            </svg>
        )
    }
    const ext = name.includes(".") ? name.split(".").pop()!.toLowerCase() : ""
    return (
        <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#9ca3af" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" fill="#f3f4f6" />
            <polyline points="14 2 14 8 20 8" />
            {ext && <text x="12" y="17" fontSize="6" textAnchor="middle" fill="#6b7280" stroke="none" fontFamily="ui-monospace,monospace">{ext.slice(0, 4)}</text>}
        </svg>
    )
}

export default function ProjectFileExplorer({ projectId }: Props) {
    const [path,     setPath]     = useState("")   // relative path, "" = root
    const [entries,  setEntries]  = useState<FileEntry[]>([])
    const [loading,  setLoading]  = useState(true)
    const [error,    setError]    = useState<string | null>(null)
    const [dragOver, setDragOver] = useState(false)
    const [uploading,setUploading]= useState(false)
    const fileInputRef = useRef<HTMLInputElement>(null)
    const dragDepth    = useRef(0)

    const load = useCallback(async (p: string) => {
        setLoading(true); setError(null)
        try {
            const res = await apiFetch(`/projects/${projectId}/files?path=${encodeURIComponent(p)}`)
            if (!res.ok) { setError((await res.json().catch(() => null))?.error ?? "Failed to load files."); setEntries([]); return }
            const data = await res.json()
            setEntries(data.entries ?? [])
        } catch { setError("Could not reach ARI."); setEntries([]) }
        finally { setLoading(false) }
    }, [projectId])

    useEffect(() => { load(path) }, [load, path])

    const crumbs = path ? path.split("/").filter(Boolean) : []

    function goTo(index: number) {
        setPath(crumbs.slice(0, index + 1).join("/"))
    }

    async function uploadFiles(files: FileList | File[]) {
        const list = Array.from(files)
        if (!list.length) return
        setUploading(true)
        try {
            const fd = new FormData()
            for (const f of list) fd.append("files", f)
            const res = await apiFetch(`/projects/${projectId}/files?path=${encodeURIComponent(path)}`, { method: "POST", body: fd })
            if (!res.ok) { setError((await res.json().catch(() => null))?.error ?? "Upload failed."); return }
            await load(path)
        } catch { setError("Upload failed — could not reach ARI.") }
        finally { setUploading(false) }
    }

    async function deleteEntry(e: React.MouseEvent, entry: FileEntry) {
        e.stopPropagation()
        if (!confirm(`Delete "${entry.name}"?${entry.isDir ? " This deletes the whole folder." : ""}`)) return
        const entryPath = path ? `${path}/${entry.name}` : entry.name
        const res = await apiFetch(`/projects/${projectId}/files?path=${encodeURIComponent(entryPath)}`, { method: "DELETE" })
        if (!res.ok) { setError((await res.json().catch(() => null))?.error ?? "Delete failed."); return }
        await load(path)
    }

    function openEntry(entry: FileEntry) {
        if (entry.isDir) { setPath(path ? `${path}/${entry.name}` : entry.name); return }
        const entryPath = path ? `${path}/${entry.name}` : entry.name
        window.open(tokenUrl(`/projects/${projectId}/files/content?path=${encodeURIComponent(entryPath)}`), "_blank")
    }

    return (
        <div
            className={`file-explorer${dragOver ? " file-explorer--drag" : ""}`}
            onDragEnter={e => { e.preventDefault(); dragDepth.current++; if (e.dataTransfer.types.includes("Files")) setDragOver(true) }}
            onDragOver={e => { if (e.dataTransfer.types.includes("Files")) e.preventDefault() }}
            onDragLeave={e => { e.preventDefault(); dragDepth.current--; if (dragDepth.current <= 0) { dragDepth.current = 0; setDragOver(false) } }}
            onDrop={e => {
                e.preventDefault()
                dragDepth.current = 0
                setDragOver(false)
                if (e.dataTransfer.files?.length) uploadFiles(e.dataTransfer.files)
            }}
        >
            <div className="file-explorer-drop-overlay visible" style={{ opacity: dragOver ? 1 : 0 }}>
                Drop to upload{path ? ` into ${crumbs[crumbs.length - 1]}` : ""}
            </div>

            <div className="breadcrumb" style={{ padding: "10px 12px 0" }}>
                <button className="btn-back" onClick={() => setPath("")} disabled={!crumbs.length}>Root</button>
                {crumbs.map((c, i) => (
                    <span key={i}>
                        <span className="breadcrumb-sep">/</span>
                        <button className="btn-back" onClick={() => goTo(i)} disabled={i === crumbs.length - 1}>{c}</button>
                    </span>
                ))}
                <span style={{ marginLeft: "auto" }}>
                    <button type="button" className="btn-secondary btn-add-att" disabled={uploading} onClick={() => fileInputRef.current?.click()}>
                        {uploading ? "Uploading…" : "Upload files"}
                    </button>
                    <input
                        ref={fileInputRef} type="file" multiple style={{ display: "none" }}
                        onChange={e => { if (e.target.files?.length) uploadFiles(e.target.files); e.target.value = "" }}
                    />
                </span>
            </div>

            {error && <p className="form-error" style={{ padding: "8px 12px 0" }}>{error}</p>}

            {loading ? (
                <div className="file-explorer-empty"><span>Loading…</span></div>
            ) : entries.length === 0 ? (
                <div className="file-explorer-empty">
                    <span>Empty folder — drag files here or click Upload files</span>
                </div>
            ) : (
                <div className="file-grid">
                    {entries.map(entry => (
                        <div key={entry.name} className="file-grid-item" onClick={() => openEntry(entry)} title={entry.name}>
                            <div className="file-grid-icon">
                                <FileIcon isDir={entry.isDir} name={entry.name} />
                                <button className="file-grid-remove" onClick={e => deleteEntry(e, entry)} title="Delete">×</button>
                            </div>
                            <span className="file-grid-name">{entry.name}</span>
                            {!entry.isDir && <span className="field-optional" style={{ fontSize: 10 }}>{formatSize(entry.size)}</span>}
                        </div>
                    ))}
                </div>
            )}
        </div>
    )
}
