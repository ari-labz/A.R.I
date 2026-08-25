import { useState, useEffect, useCallback } from "react"
import type { Project } from "../hooks/useThreads"
import { apiFetch, getToken } from "../auth"
import { env } from "../env"

type SyncState = "idle" | "checking" | "up-to-date" | "ahead" | "behind" | "conflict" | "dirty" | "syncing" | "error" | "no-local-path" | "uninitialized"
interface SyncStatus { state: SyncState; ahead?: number; behind?: number; message?: string }

interface Props {
    projects:         Project[]
    onProjectCreated: () => void
}


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

    const [localPaths,       setLocalPaths]       = useState<Record<string, string | null>>({})
    const [editPath,         setEditPath]         = useState<string | null>(null)
    const [syncStatuses,     setSyncStatuses]     = useState<Record<string, SyncStatus>>({})
    const [syncing,          setSyncing]          = useState(false)
    const [autoSync,         setAutoSync]         = useState(false)

    const isElectron = !!window.electronBridge

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
        setAutoSync(localStorage.getItem(`ari-autosync-${p.id}`) === "1")
        if (isElectron) checkSyncStatus(p.id, localPaths[p.id] ?? null)
    }

    async function pickEditFolder() {
        if (!selected) return
        const path = await env.pickFolder()
        if (!path) return
        setEditPath(path)
        await env.setLocalPath(selected.id, path)
        setLocalPaths(prev => ({ ...prev, [selected.id]: path }))
        checkSyncStatus(selected.id, path)
    }

    async function clearEditFolder() {
        if (!selected) return
        setEditPath(null)
        await env.setLocalPath(selected.id, null)
        setLocalPaths(prev => ({ ...prev, [selected.id]: null }))
        setSyncStatuses(prev => ({ ...prev, [selected.id]: { state: "no-local-path" } }))
    }

    const checkSyncStatus = useCallback(async (projectId: string, localPath: string | null) => {
        if (!isElectron || !localPath) {
            setSyncStatuses(prev => ({ ...prev, [projectId]: { state: "no-local-path" } }))
            return
        }
        setSyncStatuses(prev => ({ ...prev, [projectId]: { state: "checking" } }))
        try {
            const result = await window.electronBridge!.syncStatus!({ projectId, localPath, token: getToken() })
            setSyncStatuses(prev => ({ ...prev, [projectId]: result as SyncStatus }))
        } catch (e: unknown) {
            setSyncStatuses(prev => ({ ...prev, [projectId]: { state: "error", message: String(e) } }))
        }
    }, [isElectron])

    async function handleSync() {
        if (!selected || syncing) return
        const localPath = localPaths[selected.id] ?? null
        if (!localPath) return
        setSyncing(true)
        setSyncStatuses(prev => ({ ...prev, [selected.id]: { state: "syncing" } }))
        try {
            const result = await window.electronBridge!.syncRun!({ projectId: selected.id, localPath, token: getToken() })
            setSyncStatuses(prev => ({ ...prev, [selected.id]: result as SyncStatus }))
        } catch (e: unknown) {
            setSyncStatuses(prev => ({ ...prev, [selected.id]: { state: "error", message: String(e) } }))
        } finally {
            setSyncing(false)
        }
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

    // ── File icon (large, Finder-style) ──────────────────────────────────────────

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

                {/* ── App settings (Electron only — local path preferred over server path) ── */}
                {isElectron && (
                    <div className="project-section">
                        <div className="project-section-header">
                            <h2>App settings</h2>
                            <span className="field-optional">Stored on this device only</span>
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
                        {editPath && selected && (() => {
                            const ss = syncStatuses[selected.id] ?? { state: "idle" }
                            const label: Record<string, string> = {
                                "idle":         "Check status",
                                "checking":     "Checking…",
                                "up-to-date":   "Synced",
                                "dirty":        "Unsynced changes",
                                "ahead":        `${ss.ahead} commit${ss.ahead === 1 ? "" : "s"} ahead`,
                                "behind":       `${ss.behind} commit${ss.behind === 1 ? "" : "s"} behind`,
                                "conflict":     "Conflict — manual resolve needed",
                                "syncing":      "Syncing…",
                                "error":        `Sync error${ss.message ? `: ${ss.message}` : ""}`,
                                "no-local-path":"Set a local path to sync",
                                "uninitialized":"Never synced",
                            }
                            const badge: Record<string, string> = {
                                "up-to-date": "sync-badge-ok",
                                "dirty":      "sync-badge-ahead",
                                "ahead":      "sync-badge-ahead",
                                "behind":     "sync-badge-behind",
                                "conflict":   "sync-badge-conflict",
                                "error":      "sync-badge-error",
                            }
                            const canSync = ["idle", "up-to-date", "dirty", "ahead", "behind", "uninitialized"].includes(ss.state)
                            const busy    = ss.state === "checking" || ss.state === "syncing" || syncing
                            return (<>
                                <div className="sync-row">
                                    <span className={`sync-badge ${badge[ss.state] ?? ""}`}>
                                        {label[ss.state] ?? ss.state}
                                    </span>
                                    <button
                                        type="button"
                                        className="btn-secondary"
                                        disabled={busy || ss.state === "conflict"}
                                        onClick={canSync ? handleSync : () => checkSyncStatus(selected.id, editPath)}
                                    >
                                        {busy ? "Working…" : canSync ? "Sync now" : "Refresh"}
                                    </button>
                                </div>
                                <label className="sync-autosync-row">
                                    <input
                                        type="checkbox"
                                        checked={autoSync}
                                        onChange={e => {
                                            const on = e.target.checked
                                            setAutoSync(on)
                                            if (on) localStorage.setItem(`ari-autosync-${selected.id}`, "1")
                                            else localStorage.removeItem(`ari-autosync-${selected.id}`)
                                        }}
                                    />
                                    Auto-sync at the start of each conversation
                                </label>
                            </>)
                        })()}
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
