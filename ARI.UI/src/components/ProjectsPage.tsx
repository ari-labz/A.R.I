import { useState, useRef, useEffect } from "react"
import type { Project } from "../hooks/useThreads"
import { env } from "../env"

interface Props {
    projects:         Project[]
    onProjectCreated: () => void
}

interface AttachmentEntry { name: string }

export default function ProjectsPage({ projects, onProjectCreated }: Props) {
    const [showForm,          setShowForm]          = useState(false)
    const [name,              setName]              = useState("")
    const [description,       setDescription]       = useState("")
    const [instructions,      setInstructions]      = useState("")
    const [forceCode,         setForceCode]         = useState(true)
    const [saving,            setSaving]            = useState(false)
    const [error,             setError]             = useState<string | null>(null)

    const [selected,          setSelected]          = useState<Project | null>(null)
    const [editName,          setEditName]          = useState("")
    const [editDescription,   setEditDescription]   = useState("")
    const [editInstructions,  setEditInstructions]  = useState("")
    const [editForceCode,     setEditForceCode]     = useState(true)
    const [editSaving,        setEditSaving]        = useState(false)
    const [editError,         setEditError]         = useState<string | null>(null)
    const [attachments,       setAttachments]       = useState<AttachmentEntry[]>([])
    const [attUploading,      setAttUploading]      = useState(false)

    // Local paths are stored per-machine in Electron, never on the server
    const [localPaths,        setLocalPaths]        = useState<Record<string, string | null>>({})
    const [editPath,          setEditPath]          = useState<string | null>(null)

    const fileInputRef = useRef<HTMLInputElement>(null)
    const isElectron   = !!window.electronBridge

    // Load local paths for all projects from this machine's electron-store
    useEffect(() => {
        if (!isElectron) return
        Promise.all(projects.map(p => env.getLocalPath(p.id).then(path => ({ id: p.id, path }))))
            .then(results => {
                const map: Record<string, string | null> = {}
                results.forEach(r => { map[r.id] = r.path })
                setLocalPaths(map)
            })
    }, [projects, isElectron])

    // ── Create form ───────────────────────────────────────────────────────────────

    async function handleCreate(e: React.FormEvent) {
        e.preventDefault()
        if (!name.trim()) return
        setSaving(true); setError(null)
        try {
            const res = await fetch("/api/projects", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: name.trim(), description: description.trim(), instructions: instructions.trim(), forceCodePipeline: forceCode }),
            })
            if (!res.ok) { setError((await res.json().catch(() => null))?.error ?? "Failed to create project."); return }
            setName(""); setDescription(""); setInstructions(""); setForceCode(true)
            setShowForm(false)
            onProjectCreated()
        } catch { setError("Could not reach ARI.") }
        finally { setSaving(false) }
    }

    function handleCancelCreate() {
        setShowForm(false)
        setName(""); setDescription(""); setInstructions(""); setForceCode(true); setError(null)
    }

    // ── Project detail ────────────────────────────────────────────────────────────

    async function openProject(p: Project) {
        setSelected(p)
        setEditName(p.name)
        setEditDescription(p.description)
        setEditInstructions(p.instructions)
        setEditPath(localPaths[p.id] ?? null)
        setEditForceCode(p.forceCodePipeline)
        setEditError(null)
        await loadAttachments(p.id)
    }

    async function loadAttachments(projectId: string) {
        try {
            const res = await fetch(`/api/projects/${projectId}/attachments`)
            if (res.ok) setAttachments(await res.json())
        } catch { /* ignore */ }
    }

    // Local path saves immediately — independent of the server Save button
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
            const res = await fetch(`/api/projects/${selected.id}`, {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ name: editName.trim(), description: editDescription.trim(), instructions: editInstructions.trim(), forceCodePipeline: editForceCode }),
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
        await fetch(`/api/projects/${selected.id}`, { method: "DELETE" })
        setSelected(null)
        onProjectCreated()
    }

    async function handleAttachFile(e: React.ChangeEvent<HTMLInputElement>) {
        if (!selected || !e.target.files?.length) return
        setAttUploading(true)
        for (const file of [...e.target.files]) {
            const fd = new FormData(); fd.append("file", file)
            await fetch(`/api/projects/${selected.id}/attachments`, { method: "POST", body: fd })
        }
        e.target.value = ""
        await loadAttachments(selected.id)
        setAttUploading(false)
    }

    async function handleRemoveAttachment(name: string) {
        if (!selected) return
        await fetch(`/api/projects/${selected.id}/attachments/${encodeURIComponent(name)}`, { method: "DELETE" })
        await loadAttachments(selected.id)
    }

    // ── Render ────────────────────────────────────────────────────────────────────

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

                {/* ── Project settings (server) ── */}
                <div className="project-section">
                    <div className="project-section-header">
                        <h2>Project settings</h2>
                        <span className="field-optional">Stored on the server — shared across all devices</span>
                    </div>
                    <form id="project-form" onSubmit={handleSave}>
                        <label>
                            Name
                            <input type="text" value={editName} onChange={e => setEditName(e.target.value)} required />
                        </label>
                        <label>
                            Description <span className="field-optional">(optional)</span>
                            <input type="text" value={editDescription} onChange={e => setEditDescription(e.target.value)} placeholder="Short description for your own reference" />
                        </label>
                        <label>
                            Instructions <span className="field-optional">(injected into every conversation)</span>
                            <textarea value={editInstructions} onChange={e => setEditInstructions(e.target.value)} rows={5}
                                placeholder={"Coding standards or preferences injected into every conversation."} />
                        </label>
                        <div className="toggle-row">
                            <div className="toggle-label">
                                <span>Force Code pipeline</span>
                                <span className="field-optional">Skip classification — always route to Code agent</span>
                            </div>
                            <button
                                type="button"
                                className={`ios-toggle${editForceCode ? " on" : ""}`}
                                onClick={() => setEditForceCode(v => !v)}
                                aria-pressed={editForceCode}
                            />
                        </div>

                        <div className="project-section-header" style={{ marginTop: "20px" }}>
                            <h2>Attachments</h2>
                            <span className="field-optional">Attached to every new thread in this project</span>
                        </div>
                        {attachments.length > 0 && (
                            <ul className="project-att-list">
                                {attachments.map(a => (
                                    <li key={a.name} className="project-att-item">
                                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/>
                                        </svg>
                                        <span>{a.name}</span>
                                        <button className="att-remove" onClick={() => handleRemoveAttachment(a.name)}>×</button>
                                    </li>
                                ))}
                            </ul>
                        )}
                        <button
                            type="button"
                            className="btn-secondary btn-add-att"
                            disabled={attUploading}
                            onClick={() => fileInputRef.current?.click()}
                        >
                            {attUploading ? "Uploading…" : "+ Add file"}
                        </button>
                        <input ref={fileInputRef} type="file" multiple style={{ display: "none" }} onChange={handleAttachFile} />

                        {editError && <p className="form-error">{editError}</p>}
                        <div className="form-actions">
                            <button type="submit" className="btn-primary" disabled={editSaving || !editName.trim()}>
                                {editSaving ? "Saving…" : "Save changes"}
                            </button>
                        </div>
                    </form>
                </div>

                {/* ── App settings (local, Electron only) ── */}
                {isElectron && (
                    <div className="project-section">
                        <div className="project-section-header">
                            <h2>App settings</h2>
                            <span className="field-optional">Stored on this device only — not synced</span>
                        </div>
                        <label>
                            Local path
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
                    <div className="toggle-row">
                        <div className="toggle-label">
                            <span>Force Code pipeline</span>
                            <span className="field-optional">Skip classification — always route to Code agent</span>
                        </div>
                        <button
                            type="button"
                            className={`ios-toggle${forceCode ? " on" : ""}`}
                            onClick={() => setForceCode(v => !v)}
                            aria-pressed={forceCode}
                        />
                    </div>
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
                    <span>Projects let you attach instructions to your conversations.</span>
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
                                <span className="project-card-name">{p.name}</span>
                                {p.description && <span className="project-card-desc">{p.description}</span>}
                                {isElectron && (
                                    localPaths[p.id]
                                        ? <span className="project-card-path">{localPaths[p.id]}</span>
                                        : <span className="project-card-path project-card-path--unavailable">
                                            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{display:"inline",verticalAlign:"middle",marginRight:"4px",marginTop:"-1px"}}>
                                                <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
                                            </svg>
                                            Not available on this device
                                          </span>
                                )}
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
