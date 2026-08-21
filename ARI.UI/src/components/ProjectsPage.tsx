import { useState, useRef, useEffect } from "react"
import type { Project } from "../hooks/useThreads"
import { apiFetch } from "../auth"
import { env } from "../env"

interface Props {
    projects:         Project[]
    onProjectCreated: () => void
}

interface FileEntry { name: string; isImage?: boolean; mimeType?: string }

export default function ProjectsPage({ projects, onProjectCreated }: Props) {
    const [showForm,         setShowForm]         = useState(false)
    const [name,             setName]             = useState("")
    const [description,      setDescription]      = useState("")
    const [instructions,     setInstructions]     = useState("")
    const [category,         setCategory]         = useState("")
    const [saving,           setSaving]           = useState(false)
    const [error,            setError]            = useState<string | null>(null)

    const [selected,         setSelected]         = useState<Project | null>(null)
    const [editName,         setEditName]         = useState("")
    const [editDescription,  setEditDescription]  = useState("")
    const [editInstructions, setEditInstructions] = useState("")
    const [editCategory,     setEditCategory]     = useState("")
    const [editSaving,       setEditSaving]       = useState(false)
    const [editError,        setEditError]        = useState<string | null>(null)

    const [files,            setFiles]            = useState<FileEntry[]>([])
    const [uploading,        setUploading]        = useState(false)
    const [dragging,         setDragging]         = useState(false)

    const [localPaths,       setLocalPaths]       = useState<Record<string, string | null>>({})
    const [editPath,         setEditPath]         = useState<string | null>(null)

    const fileInputRef = useRef<HTMLInputElement>(null)
    const isElectron   = !!window.electronBridge

    useEffect(() => {
        if (!isElectron) return
        Promise.all(projects.map(p => env.getLocalPath(p.id).then(path => ({ id: p.id, path }))))
            .then(results => {
                const map: Record<string, string | null> = {}
                results.forEach(r => { map[r.id] = r.path })
                setLocalPaths(map)
            })
    }, [projects, isElectron])

    // ── Create ────────────────────────────────────────────────────────────────────

    async function handleCreate(e: React.FormEvent) {
        e.preventDefault()
        if (!name.trim()) return
        setSaving(true); setError(null)
        try {
            const res = await apiFetch("/projects", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: name.trim(), description: description.trim(), instructions: instructions.trim(), category: category.trim() }),
            })
            if (!res.ok) { setError((await res.json().catch(() => null))?.error ?? "Failed to create project."); return }
            setName(""); setDescription(""); setInstructions(""); setCategory("")
            setShowForm(false)
            onProjectCreated()
        } catch { setError("Could not reach ARI.") }
        finally { setSaving(false) }
    }

    function handleCancelCreate() {
        setShowForm(false)
        setName(""); setDescription(""); setInstructions(""); setCategory(""); setError(null)
    }

    // ── Detail ────────────────────────────────────────────────────────────────────

    async function openProject(p: Project) {
        setSelected(p)
        setEditName(p.name)
        setEditDescription(p.description)
        setEditInstructions(p.instructions)
        setEditCategory(p.category)
        setEditPath(localPaths[p.id] ?? null)
        setEditError(null)
        await loadFiles(p.id)
    }

    async function loadFiles(projectId: string) {
        try {
            const res = await apiFetch(`/projects/${projectId}/attachments`)
            if (res.ok) setFiles(await res.json())
        } catch { /* ignore */ }
    }

    async function pickEditFolder() {
        if (!selected) return
        const path = await env.pickFolder()
        if (!path) return
        setEditPath(path)
        await env.setLocalPath(selected.id, path)
        setLocalPaths(prev => ({ ...prev, [selected.id]: path }))
    }

    async function clearEditFolder() {
        if (!selected) return
        setEditPath(null)
        await env.setLocalPath(selected.id, null)
        setLocalPaths(prev => ({ ...prev, [selected.id]: null }))
    }

    async function handleSave(e: React.FormEvent) {
        e.preventDefault()
        if (!selected || !editName.trim()) return
        setEditSaving(true); setEditError(null)
        try {
            const res = await apiFetch(`/projects/${selected.id}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: editName.trim(), description: editDescription.trim(), instructions: editInstructions.trim(), category: editCategory.trim(), backend: selected.backend }),
            })
            if (!res.ok) { setEditError((await res.json().catch(() => null))?.error ?? "Failed to save."); return }
            setSelected(await res.json())
            onProjectCreated()
        } catch { setEditError("Could not reach ARI.") }
        finally { setEditSaving(false) }
    }

    async function handleDelete() {
        if (!selected) return
        if (!confirm(`Delete "${selected.name}"? This cannot be undone.`)) return
        await apiFetch(`/projects/${selected.id}`, { method: "DELETE" })
        setSelected(null)
        onProjectCreated()
    }

    // ── File upload ───────────────────────────────────────────────────────────────

    async function uploadFiles(projectId: string, fileList: FileList | File[]) {
        setUploading(true)
        for (const file of [...fileList]) {
            const fd = new FormData(); fd.append("file", file)
            await apiFetch(`/projects/${projectId}/attachments`, { method: "POST", body: fd })
        }
        await loadFiles(projectId)
        setUploading(false)
    }

    async function handleFileInput(e: React.ChangeEvent<HTMLInputElement>) {
        if (!selected || !e.target.files?.length) return
        await uploadFiles(selected.id, e.target.files)
        e.target.value = ""
    }

    async function handleRemoveFile(name: string) {
        if (!selected) return
        await apiFetch(`/projects/${selected.id}/attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        await loadFiles(selected.id)
    }

    function onDragOver(e: React.DragEvent) { e.preventDefault(); setDragging(true) }
    function onDragLeave()                   { setDragging(false) }
    async function onDrop(e: React.DragEvent) {
        e.preventDefault(); setDragging(false)
        if (selected && e.dataTransfer.files.length) await uploadFiles(selected.id, e.dataTransfer.files)
    }

    // ── File icon (large, Finder-style) ──────────────────────────────────────────

    function FileIcon({ name }: { name: string }) {
        const ext = name.split(".").pop()?.toLowerCase() ?? ""
        const isImage = ["png","jpg","jpeg","gif","webp","svg","ico","bmp"].includes(ext)
        const isCode  = ["ts","tsx","js","jsx","cs","py","json","yaml","yml","xml","html","css","sh","md"].includes(ext)
        const isPdf   = ext === "pdf"

        if (isImage) return (
            <svg width="52" height="52" viewBox="0 0 52 52" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="6" y="4" width="40" height="44" rx="4" fill="#e8f4fb" stroke="#b3d4e8" strokeWidth="1.5"/>
                <rect x="10" y="10" width="32" height="22" rx="2" fill="#c5e3f5"/>
                <circle cx="16" cy="16" r="3" fill="#f0c060"/>
                <path d="M10 28l10-8 8 6 6-4 8 6v6a2 2 0 0 1-2 2H12a2 2 0 0 1-2-2v-6z" fill="#6ab8e0"/>
                <rect x="10" y="36" width="20" height="2" rx="1" fill="#b3d4e8"/>
                <rect x="10" y="40" width="14" height="2" rx="1" fill="#b3d4e8"/>
            </svg>
        )
        if (isPdf) return (
            <svg width="52" height="52" viewBox="0 0 52 52" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="6" y="4" width="40" height="44" rx="4" fill="#fff0f0" stroke="#f5b3b3" strokeWidth="1.5"/>
                <path d="M30 4v12h12" fill="none" stroke="#f5b3b3" strokeWidth="1.5"/>
                <path d="M30 4l12 12H30V4z" fill="#fde0e0"/>
                <rect x="10" y="22" width="32" height="14" rx="2" fill="#e55"/>
                <text x="26" y="33" textAnchor="middle" fill="white" fontSize="9" fontWeight="bold" fontFamily="sans-serif">PDF</text>
                <rect x="10" y="40" width="20" height="2" rx="1" fill="#f5b3b3"/>
                <rect x="10" y="44" width="14" height="2" rx="1" fill="#f5b3b3"/>
            </svg>
        )
        if (isCode) return (
            <svg width="52" height="52" viewBox="0 0 52 52" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="6" y="4" width="40" height="44" rx="4" fill="#f0f4ff" stroke="#b3c4f5" strokeWidth="1.5"/>
                <path d="M30 4v12h12" fill="none" stroke="#b3c4f5" strokeWidth="1.5"/>
                <path d="M30 4l12 12H30V4z" fill="#dce6ff"/>
                <text x="14" y="32" fill="#6080d0" fontSize="8" fontFamily="monospace" fontWeight="bold">{"</ >"}</text>
                <rect x="10" y="38" width="22" height="2" rx="1" fill="#b3c4f5"/>
                <rect x="10" y="42" width="16" height="2" rx="1" fill="#b3c4f5"/>
            </svg>
        )
        return (
            <svg width="52" height="52" viewBox="0 0 52 52" fill="none" xmlns="http://www.w3.org/2000/svg">
                <rect x="6" y="4" width="40" height="44" rx="4" fill="#f5f7fa" stroke="#cdd5e0" strokeWidth="1.5"/>
                <path d="M30 4v12h12" fill="none" stroke="#cdd5e0" strokeWidth="1.5"/>
                <path d="M30 4l12 12H30V4z" fill="#e4e9f0"/>
                <rect x="12" y="22" width="28" height="2" rx="1" fill="#c8d0dc"/>
                <rect x="12" y="27" width="28" height="2" rx="1" fill="#c8d0dc"/>
                <rect x="12" y="32" width="20" height="2" rx="1" fill="#c8d0dc"/>
                <rect x="12" y="37" width="24" height="2" rx="1" fill="#c8d0dc"/>
                <rect x="12" y="42" width="16" height="2" rx="1" fill="#c8d0dc"/>
            </svg>
        )
    }

    // ── Project detail view ───────────────────────────────────────────────────────

    if (selected) {
        return (
            <div id="projects-page">
                <div id="projects-header">
                    <div className="breadcrumb">
                        <button className="btn-back" onClick={() => setSelected(null)}>
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M15 18l-6-6 6-6"/>
                            </svg>
                            Projects
                        </button>
                        <span className="breadcrumb-sep">/</span>
                        <span>{selected.name}</span>
                    </div>
                    <button className="btn-danger" onClick={handleDelete}>Delete project</button>
                </div>

                {/* ── Project settings ── */}
                <div className="project-section">
                    <div className="project-section-header">
                        <h2>Project settings</h2>
                        <span className="field-optional">Stored on the server — shared across all devices</span>
                    </div>
                    <form className="project-detail-form" onSubmit={handleSave}>
                        <label style={{ marginBottom: 12 }}>
                            Name
                            <input type="text" value={editName} onChange={e => setEditName(e.target.value)} required />
                        </label>
                        <label style={{ marginBottom: 12 }}>
                            Description <span className="field-optional">(optional)</span>
                            <input type="text" value={editDescription} onChange={e => setEditDescription(e.target.value)} placeholder="Short description for your own reference" />
                        </label>
                        <label style={{ marginBottom: 12 }}>
                            Instructions <span className="field-optional">(injected into every conversation)</span>
                            <textarea value={editInstructions} onChange={e => setEditInstructions(e.target.value)} rows={4}
                                placeholder="Coding standards or preferences injected into every conversation." />
                        </label>
                        <label style={{ marginBottom: 16 }}>
                            Category <span className="field-optional">(optional — for your own search/sort)</span>
                            <input type="text" value={editCategory} onChange={e => setEditCategory(e.target.value)} placeholder="e.g. Book, Game, DND Campaign" />
                        </label>
                        {selected.rootPath && (
                            <div className="project-meta-row" style={{ marginBottom: 16 }}>
                                <span className="project-meta-path" style={{ opacity: 0.55 }}>{selected.rootPath}</span>
                            </div>
                        )}
                        {editError && <p className="form-error">{editError}</p>}
                        <div className="form-actions">
                            <button type="submit" className="btn-primary" disabled={editSaving || !editName.trim()}>
                                {editSaving ? "Saving…" : "Save changes"}
                            </button>
                        </div>
                    </form>
                </div>

                {/* ── File explorer ── */}
                <div className="project-section">
                    <div className="project-section-header">
                        <h2>Files</h2>
                        <button
                            type="button"
                            className="btn-secondary btn-add-att"
                            disabled={uploading}
                            onClick={() => fileInputRef.current?.click()}
                        >
                            {uploading ? "Uploading…" : "+ Add file"}
                        </button>
                        <input ref={fileInputRef} type="file" multiple style={{ display: "none" }} onChange={handleFileInput} />
                    </div>
                    <div
                        className={`file-explorer${dragging ? " file-explorer--drag" : ""}`}
                        onDragOver={onDragOver}
                        onDragLeave={onDragLeave}
                        onDrop={onDrop}
                    >
                        {files.length === 0 ? (
                            <div className="file-explorer-empty">
                                <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" style={{ opacity: 0.25 }}>
                                    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/>
                                </svg>
                                <span>Drop files here or click Add file</span>
                            </div>
                        ) : (
                            <div className="file-grid">
                                {files.map(f => (
                                    <div key={f.name} className="file-grid-item" title={f.name}>
                                        <div className="file-grid-icon">
                                            <FileIcon name={f.name} />
                                            <button
                                                type="button"
                                                className="file-grid-remove"
                                                title="Remove"
                                                onClick={() => handleRemoveFile(f.name)}
                                            >×</button>
                                        </div>
                                        <span className="file-grid-name">{f.name}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                        <div className={`file-explorer-drop-overlay${dragging ? " visible" : ""}`}>
                            Drop to upload
                        </div>
                    </div>
                </div>

                {/* ── App settings (Electron only — local path preferred over server path) ── */}
                {isElectron && (
                    <div className="project-section">
                        <div className="project-section-header">
                            <h2>App settings</h2>
                            <span className="field-optional">Stored on this device only — not synced</span>
                        </div>
                        <label>
                            Local path <span className="field-optional">(preferred over server path when set)</span>
                            <div className="folder-picker-row">
                                <span className="folder-picker-path">
                                    {editPath ?? <span className="project-unavailable">Not available on this machine</span>}
                                </span>
                                <button type="button" className="btn-secondary btn-pick-folder" onClick={pickEditFolder}>Browse…</button>
                                {editPath && <button type="button" className="btn-secondary btn-pick-folder" onClick={clearEditFolder}>Clear</button>}
                            </div>
                        </label>
                    </div>
                )}
            </div>
        )
    }

    // ── Project list ──────────────────────────────────────────────────────────────

    return (
        <div id="projects-page">
            <div id="projects-header">
                <h1>Projects</h1>
                {!showForm && (
                    <button id="btn-new-project" onClick={() => setShowForm(true)}>
                        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>
                        </svg>
                        New project
                    </button>
                )}
            </div>

            {showForm && (
                <form id="project-form" onSubmit={handleCreate}>
                    <h2>New project</h2>
                    <label>
                        Name
                        <input type="text" value={name} onChange={e => setName(e.target.value)} placeholder="My project" autoFocus required />
                    </label>
                    <label>
                        Description <span className="field-optional">(optional)</span>
                        <input type="text" value={description} onChange={e => setDescription(e.target.value)} placeholder="Short description for your own reference" />
                    </label>
                    <label>
                        Instructions <span className="field-optional">(optional)</span>
                        <textarea value={instructions} onChange={e => setInstructions(e.target.value)}
                            placeholder={"Coding standards or preferences injected into every conversation.\n\nExample: Use TypeScript strict mode. Prefer functional components."}
                            rows={5} />
                    </label>
                    <label>
                        Category <span className="field-optional">(optional — for your own search/sort)</span>
                        <input type="text" value={category} onChange={e => setCategory(e.target.value)} placeholder="e.g. Book, Game, DND Campaign" />
                    </label>
                    {error && <p className="form-error">{error}</p>}
                    <div className="form-actions">
                        <button type="button" className="btn-secondary" onClick={handleCancelCreate} disabled={saving}>Cancel</button>
                        <button type="submit" className="btn-primary" disabled={saving || !name.trim()}>
                            {saving ? "Creating…" : "Create project"}
                        </button>
                    </div>
                </form>
            )}

            {projects.length === 0 && !showForm ? (
                <div id="projects-empty">
                    <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                    </svg>
                    <p>No projects yet</p>
                    <span>Projects let you attach instructions and files to your conversations.</span>
                </div>
            ) : (
                <ul id="projects-list">
                    {projects.map(p => (
                        <li key={p.id} className="project-card" onClick={() => openProject(p)}>
                            <div className="project-card-icon">
                                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                                </svg>
                            </div>
                            <div className="project-card-body">
                                <span className="project-card-name">
                                    {p.name}
                                    {p.category && <span className="field-optional" style={{ marginLeft: "6px" }}>· {p.category}</span>}
                                </span>
                                {p.description && <span className="project-card-desc">{p.description}</span>}
                                {isElectron && localPaths[p.id]
                                    ? <span className="project-card-path">{localPaths[p.id]}</span>
                                    : p.rootPath
                                        ? <span className="project-card-path" style={{ opacity: 0.55 }}>{p.rootPath}</span>
                                        : null
                                }
                            </div>
                            <svg className="project-card-chevron" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M9 18l6-6-6-6"/>
                            </svg>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    )
}
