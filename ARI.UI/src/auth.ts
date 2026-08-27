const TOKEN_KEY = "ari_token"

export interface AuthUser {
    id:                 number
    username:           string
    role:               string
    displayName:        string
    mustChangePassword: boolean
}

// ── Token storage ─────────────────────────────────────────────────────────────
// Desktop (electronBridge): persisted across restarts via the bridge (30-day JWT).
// Browser: sessionStorage for the life of the tab, backed by the HttpOnly `ari_session`
// cookie the server sets at login. The cookie is what keeps the user signed in after the
// tab is closed — requests with no token still authenticate on it.

type BridgeWithAuth = typeof window.electronBridge & {
    getToken(): string | null
    setToken(token: string): void
    clearToken(): void
}

function bridge(): BridgeWithAuth | null {
    const b = window.electronBridge as BridgeWithAuth | undefined
    return b && typeof b.getToken === "function" ? b : null
}

export function getToken(): string | null {
    return bridge()?.getToken() ?? sessionStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string): void {
    const b = bridge()
    if (b) b.setToken(token)
    else   sessionStorage.setItem(TOKEN_KEY, token)
}

export function clearToken(): void {
    const b = bridge()
    if (b) b.clearToken()
    else   sessionStorage.removeItem(TOKEN_KEY)
}

// ── Fetch helpers ─────────────────────────────────────────────────────────────

export function apiFetch(input: string, init?: RequestInit): Promise<Response> {
    const token = getToken()
    if (!token) return fetch(input, init)
    const headers = new Headers(init?.headers)
    headers.set("Authorization", `Bearer ${token}`)
    return fetch(input, { ...init, headers })
}

/** Appends `?token=…` to a URL — for EventSource / WebSocket which can't set headers. */
export function tokenUrl(url: string): string {
    const token = getToken()
    if (!token) return url
    const sep = url.includes("?") ? "&" : "?"
    return `${url}${sep}token=${encodeURIComponent(token)}`
}

// ── Auth API calls ────────────────────────────────────────────────────────────

export interface LoginResult {
    token:               string
    role:                string
    displayName:         string
    mustChangePassword:  boolean
}

export async function login(username: string, password: string, isDesktop: boolean): Promise<LoginResult> {
    const res = await fetch("/auth/login", {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify({ username, password, isDesktop, deviceHint: isDesktop ? "ARI Desktop" : "Browser" }),
    })
    if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        throw new Error((data as { error?: string }).error ?? `Login failed (${res.status})`)
    }
    return res.json()
}

export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const res = await apiFetch("/auth/change-password", {
        method:  "POST",
        headers: { "Content-Type": "application/json" },
        body:    JSON.stringify({ currentPassword, newPassword }),
    })
    if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        throw new Error((data as { error?: string }).error ?? `Change failed (${res.status})`)
    }
}

/** Resolves the signed-in user from the token, or — on a fresh tab — from the session cookie. */
export async function fetchMe(): Promise<AuthUser | null> {
    try {
        const res = await apiFetch("/auth/me")
        if (!res.ok) return null
        return res.json()
    } catch {
        return null
    }
}

export async function logout(): Promise<void> {
    await apiFetch("/auth/logout", { method: "POST" }).catch(() => {})
    clearToken()
}
